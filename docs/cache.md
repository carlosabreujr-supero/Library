# Cache de leitura (Redis)

O cache é **apenas informativo**. O PostgreSQL continua sendo a fonte autoritativa: criar, devolver e cancelar empréstimos nunca leem nem confiam no cache (a reserva é um `UPDATE` condicional no banco).

## O que é cacheado

| Consulta | Área (`area`) | Invalidada por |
|---|---|---|
| `GET /books` (cada combinação página/tamanho/busca/inativos) | `books:list` | criação, alteração e desativação de livro; empréstimo, devolução e cancelamento |
| `GET /books/{id}/availability` | `availability:{id}` | alteração/desativação do livro (inclui `TotalCopies`); empréstimo, devolução e cancelamento daquele livro |

A invalidação ocorre **depois do commit** da transação. Repetições idempotentes de `POST /loans` não invalidam (não alteram estoque).

## Chaves versionadas e TTL

- Dados: `{KeyPrefix}:data:{área}:{versão}:{chave}`, com TTL `Cache:TtlSeconds` (padrão 30 s).
- Versão da área: `{KeyPrefix}:ver:{área}` (TTL 24 h). Invalidar = gravar uma nova versão; as entradas antigas ficam órfãs e expiram sozinhas. Não há varredura de chaves nem coordenação entre réplicas: todas leem a mesma versão no Redis.
- `KeyPrefix` (`library:v1`) permite mudar o formato do cache sem conflito com dados antigos: basta alterar o prefixo.
- A versão é lida antes da consulta ao banco; se uma invalidação ocorrer no meio, o valor gravado cai numa versão descartada e nunca é servido.

## Configuração

| Chave | Variável de ambiente | Padrão |
|---|---|---|
| `ConnectionStrings:Redis` | `ConnectionStrings__Redis` | `redis:6379` (compose) |
| `Cache:Enabled` | `Cache__Enabled` / `CACHE_ENABLED` | `true` |
| `Cache:TtlSeconds` | `Cache__TtlSeconds` / `CACHE_TTL_SECONDS` | `30` |
| `Cache:KeyPrefix` | `Cache__KeyPrefix` | `library:v1` |
| `Cache:CircuitBreakerSeconds` | `Cache__CircuitBreakerSeconds` | `10` |
| `Cache:TimeoutMs` | `Cache__TimeoutMs` | `500` |

`Cache:Enabled=false` desliga o cache sem outras mudanças.

## Falha do Redis (fallback)

- A API sobe e opera com o Redis fora do ar (`abortConnect=false`, timeouts curtos).
- Qualquer erro de leitura/escrita/invalidação é registrado em log (warning), contado e abre um *circuit breaker* local por `CircuitBreakerSeconds`; nesse período as consultas vão direto ao PostgreSQL, sem pagar o timeout.
- **Consistência:** se uma invalidação não puder ser feita, entradas antigas podem ser servidas por até `TtlSeconds` após o Redis voltar. Isso afeta só as consultas informativas, nunca os empréstimos.

## Métricas

`System.Diagnostics.Metrics`, medidor `LibraryLoans.Cache`, tag `cache.area` (`books` ou `availability`):
`cache.hits`, `cache.misses`, `cache.errors`, `cache.bypasses` (breaker aberto), `cache.invalidations`. Exportáveis por OpenTelemetry/dotnet-counters.
