# Idempotência do `POST /loans`

## Contrato

- O cabeçalho `Idempotency-Key` é **obrigatório** (até 200 caracteres ASCII imprimíveis, sem espaços); sem ele a API retorna 400.
- **Escopo da chave:** global. A chave é a PK de `idempotency_records`, e o banco garante a unicidade. Clientes devem usar chaves únicas (ex.: UUID por operação).
- **Hash da requisição:** SHA-256 de uma forma canônica dos campos relevantes (`bookId`, `readerId`), independente de formatação ou ordem do JSON.
- Mesma chave e mesmo hash: devolve o resultado original (mesmo status e corpo), com o header `Idempotent-Replayed: true`. Não cria empréstimo nem decrementa estoque.
- Mesma chave e hash diferente: 409.

## Atomicidade e concorrência

Tudo ocorre em **uma transação PostgreSQL**:

1. `INSERT INTO idempotency_records ... ON CONFLICT ("Key") DO NOTHING`. Se outra transação ainda não confirmada detém a chave, o PostgreSQL bloqueia a requisição até o commit ou rollback dela. Assim, só uma requisição por chave executa a regra de negócio, mesmo entre réplicas.
2. Validações, reserva atômica do exemplar, inserção do empréstimo e auditoria.
3. Atualização do registro para `Completed`, com `ResponseStatusCode`, `ResponseBody` e `LoanId`.
4. Commit.

Qualquer falha reverte a transação inteira (reserva, empréstimo, auditoria e a chave). Não existe registro `InProgress` permanente nem reserva sem empréstimo.

**Falhas de negócio (404, 409) e erros inesperados não são memorizados**: a chave é liberada, e uma nova tentativa reavalia as regras. Apenas resultados de sucesso (201) são persistidos e repetidos.

O Redis não participa da idempotência; a fonte autoritativa é o PostgreSQL.

## Política de retenção

- As chaves são mantidas por **24 horas** (`Idempotency:RetentionHours`, ou `IDEMPOTENCY_RETENTION_HOURS` no compose), contadas a partir de `CreatedAtUtc`.
- Um serviço em segundo plano (`IdempotencyPurgeService`) remove as expiradas a cada `Idempotency:PurgeIntervalMinutes` (padrão 60). O `DELETE` é idempotente, então várias réplicas executando-o em paralelo é seguro.
- Depois da expiração, a mesma chave é tratada como uma nova requisição. Clientes não devem reutilizar chaves após a janela de retenção.
- Remover a chave **não** afeta o empréstimo, que continua preservado.
