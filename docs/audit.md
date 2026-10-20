# Auditoria de domínio

A trilha de negócio fica na tabela `audit_events` do PostgreSQL e **não depende de logs técnicos** nem da retenção deles.

## Eventos

| Entidade | Ação | Dados (`data`, jsonb) |
|---|---|---|
| Book | `BookCreated` | título, autor, ISBN, total e disponíveis |
| Book | `BookUpdated` | por campo alterado: `{ from, to }` (inclui `totalCopies` e `availableCopies`) |
| Book | `BookDeactivated` | título e ISBN |
| Loan | `LoanCreated` | livro, leitor, `BorrowedAtUtc`, `DueAtUtc` |
| Loan | `LoanReturned`, `LoanCancelled` | reservados em `AuditActions`; gravados quando os casos de uso de devolução e cancelamento forem implementados |

Cada evento guarda: `EntityType`, `EntityId`, `Action`, `ActorId`, `TimestampUtc` (UTC), `CorrelationId` e `Data`.

## Garantias

- `IAuditTrail` (Application) só adiciona o evento ao `DbContext`; a implementação (Infrastructure) o grava no **mesmo `SaveChanges`/transação** da operação. Se a operação é revertida ou rejeitada, o evento não existe.
- A tabela é append-only: um trigger do PostgreSQL bloqueia `UPDATE` e `DELETE`, e a API não expõe escrita de eventos.

## Ator

Ainda não há autenticação. O ator é o valor do cabeçalho opcional `X-Actor-Id` (até 100 caracteres: letras, dígitos, `-`, `_`, `.`, `@`); na ausência ou valor inválido é registrado como **`anonymous`**. O valor **não é verificado** e não deve ser tratado como identidade autenticada.

## Consulta: `GET /audit-events`

Filtros opcionais: `entityType`, `entityId`, `action`, `actorId`, `correlationId`, `from`, `to` (ISO 8601), além de `page` e `pageSize` (máximo 100). Ordem: mais recentes primeiro.
