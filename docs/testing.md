# Testes automatizados

## Unitários (`tests/LibraryLoans.UnitTests`)
Regras de negócio da Application com repositórios fake (sem banco): livro/leitor inativo, indisponibilidade, transições inválidas, restauração única do estoque, idempotência e limite de `TotalCopies`.

```powershell
dotnet test tests\LibraryLoans.UnitTests
```

## Integração (`tests/LibraryLoans.IntegrationTests`)
Usam **PostgreSQL real** (nunca EF InMemory), com as migrations aplicadas na inicialização da fábrica de testes. A connection string vem da variável `TEST_POSTGRES`; sem ela os testes **falham** (não são ignorados).

```powershell
docker compose up -d postgres
$env:TEST_POSTGRES = "Host=localhost;Port=5433;Database=library_loans_tests;Username=library;Password=<senha do .env>"
dotnet test tests\LibraryLoans.IntegrationTests
```

- Use um banco dedicado (`library_loans_tests`), não o de desenvolvimento.
- Isolamento: cada teste cria seus próprios livros (ISBN aleatório) e leitores e só consulta os próprios ids; nenhum teste depende de ordem ou de dados anteriores, portanto podem ser repetidos sem limpar o banco.
- Concorrência: duas instâncias da API (réplicas) compartilham o banco; as requisições são liberadas por uma barreira, sem `Task.Delay`.
- A tabela `audit_events` é append-only (trigger); para limpar o banco de testes use `DROP DATABASE`/`TRUNCATE`.
