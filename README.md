# LibraryLoans API

## 1. Objetivo e arquitetura

API REST de empréstimos de livros (.NET 10, EF Core + Npgsql, PostgreSQL, Redis). O foco é **correção sob concorrência**: múltiplas réplicas
nunca emprestam o mesmo último exemplar, e requisições repetidas não duplicam empréstimos.

```
src/
  LibraryLoans.Domain          entidades e enums (Book, Reader, Loan, IdempotencyRecord, AuditEvent)
  LibraryLoans.Application     casos de uso (Commands/Queries/Handlers sem framework CQRS), abstrações, telemetria
  LibraryLoans.Infrastructure  EF Core/Npgsql, repositórios, migrations, cache Redis, health checks
  LibraryLoans.Api             Minimal APIs, Problem Details, correlation id, OpenTelemetry
tests/
  LibraryLoans.UnitTests       regras de negócio com fakes
  LibraryLoans.IntegrationTests PostgreSQL e Redis reais
docs/  k8s/
```

Dependências: Api → Application/Infrastructure → Domain. As regras de negócio não dependem de HTTP.
PostgreSQL é a fonte de verdade (estoque, idempotência, auditoria); Redis é apenas cache de leitura.

## 2. Pré-requisitos

- .NET SDK 10 e Docker (com Compose v2).
- `dotnet tool restore` instala o `dotnet-ef` (versão fixada em `dotnet-tools.json`).

## 3. Docker Compose

```powershell
Copy-Item .env.example .env      # ajuste a senha local
docker compose up --build -d     # PostgreSQL 17 + Redis 7 + migrations (serviço migrate) + API
docker compose ps
docker compose down              # encerra; o volume do PostgreSQL é preservado
docker compose down -v           # encerra e apaga os dados
```

O serviço `migrate` roda uma única vez por `up` (não por réplica da API), aplica as migrations e termina; a API só inicia depois (`service_completed_successfully`). A API sobe em `http://localhost:8080` (`API_PORT`). Os dados do PostgreSQL persistem em volume nomeado.
Portas de PostgreSQL/Redis não são publicadas; para uso no host crie um `docker-compose.override.yml` local (gitignored), ex.:
`services: { postgres: { ports: ["5433:5432"] }, redis: { ports: ["6380:6379"] } }`.

## 4. Configuração de ambiente (sem segredos)

`.env.example` é o modelo versionado; `.env` real **não** é versionado. Variáveis:

| Variável | Padrão | Uso |
|---|---|---|
| `POSTGRES_DB`, `POSTGRES_USER`, `POSTGRES_PASSWORD` | — (senha: troque) | credenciais locais |
| `API_PORT` | 8080 | porta publicada |
| `LOAN_DEFAULT_DAYS` | 14 | prazo padrão (`Loans__DefaultLoanDays`) |
| `IDEMPOTENCY_RETENTION_HOURS` | 24 | retenção das chaves |
| `CACHE_ENABLED`, `CACHE_TTL_SECONDS` | true, 30 | cache Redis |
| `HEALTH_REDIS_REQUIRED` | false | exigir Redis na readiness |
| `OTEL_SERVICE_NAME`, `OTEL_EXPORTER_OTLP_ENDPOINT` | — | exportação OpenTelemetry (opcional) |

Em runtime a API lê `ConnectionStrings__Postgres` e `ConnectionStrings__Redis` (variáveis de ambiente ou Secret no Kubernetes).

## 5. Migrations

A API **não** aplica migrations ao iniciar (seguro com várias réplicas); elas são uma etapa separada de deploy.
No Compose isso é automático: o serviço `migrate` (stage `migrate` do Dockerfile) roda `dotnet ef database update` antes da API. Para rodar manualmente
(`docker compose run --rm migrate`) ou criar novas migrations a partir do host, com o PostgreSQL acessível:

```powershell
dotnet tool restore
$env:ConnectionStrings__Postgres = "Host=localhost;Port=5433;Database=library_loans;Username=library;Password=<senha do .env>"
dotnet ef database update --project src/LibraryLoans.Infrastructure --startup-project src/LibraryLoans.Infrastructure
dotnet ef migrations add <Nome> --project src/LibraryLoans.Infrastructure --startup-project src/LibraryLoans.Infrastructure --output-dir Migrations
```

Detalhes em [docs/migrations.md](docs/migrations.md).

## 6. Testes

Os testes de integração exigem PostgreSQL real (e Redis para os de cache); sem `TEST_POSTGRES` eles **falham** (não são ignorados).

```powershell
dotnet test tests/LibraryLoans.UnitTests

$env:TEST_POSTGRES = "Host=localhost;Port=5433;Database=library_loans_tests;Username=library;Password=<senha>"
$env:TEST_REDIS    = "localhost:6380,allowAdmin=true"
dotnet test
```

A suíte aplica as migrations em um banco vazio e isola os dados de cada teste. Ver [docs/testing.md](docs/testing.md). O CI (`.github/workflows/ci.yml`) roda build, testes (com PostgreSQL e Redis como serviços) e o build das imagens Docker.

## 7. Endpoints

| Método | Rota | Descrição | Sucesso |
|---|---|---|---|
| POST | `/books` | cria livro | 201 |
| GET | `/books?page&pageSize&search&includeInactive` | lista paginada | 200 |
| GET | `/books/{id}` | detalhe | 200 |
| PATCH | `/books/{id}` | altera título/autor/ISBN/total | 200 |
| DELETE | `/books/{id}` | **desativa** (não apaga) | 204 |
| GET | `/books/{id}/availability` | disponibilidade | 200 |
| GET | `/books/{id}/history?page&pageSize&status` | histórico de empréstimos | 200 |
| POST | `/users` | cadastra leitor | 201 |
| GET | `/users/{id}/loans?page&pageSize&status` | histórico do leitor | 200 |
| POST | `/loans` | cria empréstimo (header `Idempotency-Key` obrigatório) | 201 |
| POST | `/loans/{id}/return` | devolve | 200 |
| POST | `/loans/{id}/cancel` | cancela | 200 |
| GET | `/audit-events?entityType&entityId&action&actorId&correlationId&from&to&page&pageSize` | auditoria (somente leitura) | 200 |
| GET | `/health/live`, `/health/ready` | saúde | 200/503 |

Erros seguem Problem Details (RFC 9457): 400 validação, 404 inexistente, 409 conflito de negócio, 500 sem detalhes internos. O header
`X-Correlation-Id` é aceito e devolvido.

```powershell
$api = "http://localhost:8080"
$book = Invoke-RestMethod -Method Post "$api/books" -ContentType application/json -Body '{"title":"Clean Code","author":"Robert C. Martin","isbn":"9780132350884","totalCopies":1}'
$reader = Invoke-RestMethod -Method Post "$api/users" -ContentType application/json -Body '{"name":"Ana"}'
$key = [guid]::NewGuid().ToString()
$body = @{ bookId = $book.id; readerId = $reader.id } | ConvertTo-Json
$loan = Invoke-RestMethod -Method Post "$api/loans" -ContentType application/json -Headers @{ "Idempotency-Key" = $key } -Body $body   # repetir com a mesma chave devolve o mesmo resultado
Invoke-RestMethod -Method Post "$api/loans/$($loan.id)/return"
Invoke-RestMethod "$api/books/$($book.id)/availability"
```

Equivalente em curl: `curl -X POST localhost:8080/loans -H "Content-Type: application/json" -H "Idempotency-Key: $(uuidgen)" -d '{"bookId":"...","readerId":"..."}'`.

## 8. Concorrência no PostgreSQL

A reserva é uma atualização condicional atômica, sem locks em memória nem cache:

```sql
UPDATE books SET "AvailableCopies" = "AvailableCopies" - 1
WHERE "Id" = @bookId AND "IsActive" AND "AvailableCopies" > 0
RETURNING "Id";
```

Só prossegue se exatamente uma linha for afetada; reserva, inserção do empréstimo, auditoria e registro de idempotência ficam numa única
transação (qualquer falha reverte tudo). Check constraints (`0 <= AvailableCopies <= TotalCopies`) garantem o invariante no banco.
Devolução/cancelamento usam `UPDATE ... WHERE Status = 'Active'` e restauram o estoque exatamente uma vez. Funciona com qualquer número de réplicas.
O teste obrigatório dispara duas requisições paralelas (réplicas distintas, barreira sem delays) para uma única unidade: um 201 e um 409.

### Trade-offs considerados

| Estratégia | Prós | Contras | Decisão |
|---|---|---|---|
| **UPDATE condicional atômico** (`WHERE available > 0`) | uma ida ao banco, sem lock explícito nem retry, o invariante vale mesmo com várias réplicas; falha de disponibilidade vira 409 direto | SQL parametrizado fora do modelo de entidades do EF | **Escolhida** |
| Lock de linha (`SELECT ... FOR UPDATE`) | simples de raciocinar | duas idas ao banco, segura a linha durante a transação e serializa leitores | descartada |
| Token de concorrência (xmin/versão) com retry | idiomático no EF | sob disputa vira loop de retries e `DbUpdateConcurrencyException`; piora no "último exemplar" | usado só para alterações de livro (`BookConcurrencyToken`) |
| Isolamento `SERIALIZABLE` | prova de corretude pelo banco | falhas de serialização (40001) exigem retry em todo o fluxo e reduzem a vazão | descartada |
| Lock em memória / cache | — | não funciona entre réplicas | proibida |

## 8.1 Correção com 2 a 11 réplicas

A API é *stateless*: nenhuma decisão crítica usa memória do processo. Estoque (UPDATE condicional + check constraints), idempotência
(PK em `idempotency_records` + `ON CONFLICT`) e auditoria (mesma transação, trigger append-only) são decididos pelo PostgreSQL, então
adicionar ou remover réplicas não altera a corretude. Redis é só cache de leitura, invalidado após o commit; sua falha cai para o PostgreSQL.
Migrations rodam uma vez (Job/etapa de deploy), nunca por réplica. Dimensione o pool de conexões: `réplicas × pool Npgsql` deve caber em `max_connections`.
O teste de concorrência simula duas réplicas (duas instâncias da aplicação) contra o mesmo banco.

## 8.2 Ajustes de contrato em relação ao sugerido

- `GET /health` foi substituído por `/health/live` e `/health/ready`.
- `DELETE /books/{id}` desativa o livro e retorna 204; o histórico é preservado. Livro inativo não pode ser emprestado.
- `POST /loans/{id}/return` e `/cancel` retornam 200 com o empréstimo atualizado; 404 se inexistente e 409 se não estiver ativo.
- Sem autenticação: o ator da auditoria é `anonymous`.
- A chave `Idempotency-Key` tem escopo global; só resultados 201 são memorizados (falhas de negócio liberam a chave).
- `GET /books` e histórico são paginados (`page`, `pageSize`).
## 9. Idempotência

`Idempotency-Key` é obrigatório; a chave (PK global) e o hash SHA-256 do payload canônico são persistidos no PostgreSQL com o resultado.
Mesma chave e payload: mesmo resultado (`Idempotent-Replayed: true`), sem novo empréstimo ou novo decremento. Payload diferente: 409.
Requisições simultâneas são serializadas pelo `INSERT ... ON CONFLICT DO NOTHING` na mesma transação do negócio. Retenção padrão de 24 h
com expurgo periódico. Redis não participa. Ver [docs/idempotency.md](docs/idempotency.md).

## 10. Auditoria transacional

`audit_events` registra criação/alteração/desativação de livro, criação, devolução e cancelamento de empréstimo (entidade, id, ação, ator,
timestamp UTC, correlationId, dados), **na mesma transação** da operação: rollback não deixa auditoria. Um trigger torna a tabela append-only
(UPDATE/DELETE rejeitados). Sem autenticação, o ator é `anonymous`/sistema. Consulta via `GET /audit-events`. Ver [docs/audit.md](docs/audit.md).

## 11. Cache e invalidação

Redis cacheia `GET /books` e `GET /books/{id}/availability` com chaves versionadas (`{prefixo}:ver:{área}`) e TTL configurável (30 s).
Após o commit de qualquer escrita relevante (livros, empréstimos, devoluções, cancelamentos) a versão da área é incrementada, invalidando as
entradas. O cache **nunca** decide empréstimos. Se o Redis falha, a API usa o PostgreSQL (circuit breaker + métricas `LibraryLoans.Cache`).
Ver [docs/cache.md](docs/cache.md).

## 12. Health checks, métricas e traces

- `/health/live`: só o processo. `/health/ready`: PostgreSQL (obrigatório) e Redis (`Degraded` se indisponível; 503 com `HEALTH_REDIS_REQUIRED=true`).
- Métricas OpenTelemetry (`library.loans.created`, `.rejected`, `.idempotent_replays`, `.create.duration`...) sem IDs de negócio como tags.
- Traces de ASP.NET Core, Npgsql e do fluxo de empréstimo; exportação OTLP somente se `OTEL_EXPORTER_OTLP_ENDPOINT` estiver definido.
- Logs JSON com correlationId; não substituem a auditoria. Ver [docs/observability.md](docs/observability.md).

## 13. Kubernetes

[k8s/](k8s) traz Deployment (imagem/réplicas configuráveis, requests/limits, probes, encerramento gracioso), Service, ConfigMap, PDB e um Secret
**modelo** sem valores. PostgreSQL e Redis são externos; migrations rodam como etapa separada. Ver [k8s/README.md](k8s/README.md).

## 14. Limitações conhecidas e evolução para produção

- Sem autenticação/autorização: o ator da auditoria é anônimo/sistema.
- Sem retry automático de conexão: após reinício do PostgreSQL, a primeira requisição que usar uma conexão do pool pode retornar 500; as seguintes se recuperam (readiness fica 503 enquanto o banco estiver fora).
- Chave de idempotência global (não por cliente); expurgo por tempo (24 h), portanto repetições após a retenção criam novo empréstimo.
- Sem multas, reservas, renovações nem prazo por tipo de leitor (fora do escopo).
- Cache só de leitura e com TTL curto: leituras podem ficar defasadas até a invalidação/TTL; `availability` em cache é informativo.
- Migrations manuais: em produção, executá-las via Job/CI com credencial dedicada. Backups, HA do PostgreSQL e Redis gerenciado ficam fora daqui.
- Evoluções: autenticação (JWT/OIDC) e ator real na auditoria, rate limiting, HPA, NetworkPolicy, Ingress/TLS, particionamento/arquivamento de `audit_events`,
  outbox para eventos, painéis e alertas sobre as métricas.
