# Observabilidade

## Health checks

| Endpoint | Verifica | Resposta |
|---|---|---|
| `GET /health/live` | Somente que o processo responde (nenhuma dependência). | 200 |
| `GET /health/ready` | PostgreSQL (obrigatório) e Redis. | 200 Healthy/Degraded, 503 Unhealthy |

- Redis **não** é dependência obrigatória por padrão: existe fallback para o PostgreSQL (SPEC-13). Redis fora do ar
  resulta em `Degraded` (HTTP 200). Com `Health__RedisRequired=true` (`HEALTH_REDIS_REQUIRED`), passa a `Unhealthy` (503).
- O corpo traz apenas status e descrição genérica por check; nunca strings de conexão ou mensagens de exceção.
- `Health__TimeoutMs` limita cada verificação (padrão 2000 ms).

## Logs

JSON estruturado no console, com escopo (`correlationId` de `X-Correlation-Id`) e identificadores de negócio como
campos (`LoanId`, `BookId`, `ReaderId`). Segredos não são registrados. Logs técnicos **não** substituem a auditoria de
domínio (`audit_events`, ver `docs/audit.md`), que é persistida no PostgreSQL na mesma transação.

## Métricas (meter `LibraryLoans`)

| Instrumento | Tipo | Tags |
|---|---|---|
| `library.loans.created` | counter | — |
| `library.loans.rejected` | counter | `reason` (`unavailable`, `book_inactive`, `reader_inactive`, `not_found`, `idempotency_key_reuse`, `idempotency_in_progress`) |
| `library.loans.idempotent_replays` | counter | — |
| `library.loans.closed` | counter | `status` (Returned/Cancelled) |
| `library.loans.create.duration` | histogram (ms) | `outcome` |

Também são exportadas as métricas do cache (`LibraryLoans.Cache`), ASP.NET Core e Npgsql.
**Cardinalidade:** tags têm apenas domínio fechado; IDs de negócio ficam somente em spans e logs.

## Traces

Fontes: ASP.NET Core (exceto `/health*`), Npgsql (spans SQL) e `LibraryLoans` (spans `CreateLoan`, `ReserveCopy`,
`Loan.Return`/`Loan.Cancel`, com tags `library.book_id`, `library.loan_id`, `library.outcome`,
`library.rejection_reason`, `correlation.id`). O contexto W3C `traceparent` é propagado.

## Exportação (OpenTelemetry/OTLP, sem vendor específico)

| Variável | Efeito |
|---|---|
| `OTEL_EXPORTER_OTLP_ENDPOINT` | Habilita exportação OTLP de traces, métricas e logs. Vazio = desabilitado. |
| `OTEL_EXPORTER_OTLP_PROTOCOL` | `grpc` (padrão) ou `http/protobuf`. |
| `OTEL_SERVICE_NAME` | Nome do serviço (padrão `LibraryLoans.Api`). |

Qualquer coletor compatível com OTLP (OpenTelemetry Collector, Jaeger, Tempo, Prometheus via collector) funciona.
Não há dependência de Azure ou DataDog.
