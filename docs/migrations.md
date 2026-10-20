# Migrations (EF Core + Npgsql)

As migrations ficam em `src/LibraryLoans.Infrastructure/Migrations` e são versionadas no Git.
**A API nunca aplica migrations ao iniciar**; elas são executadas como etapa separada de deploy.

## Pré-requisitos

```powershell
dotnet tool restore            # instala o dotnet-ef (dotnet-tools.json)
docker compose up -d postgres  # PostgreSQL local
```

O `dotnet ef` lê a connection string da variável de ambiente `ConnectionStrings__Postgres`;
nada é gravado no código. O compose não publica a porta do PostgreSQL; para rodar o `dotnet ef`
no host, crie um `docker-compose.override.yml` local com `ports: ["5433:5432"]` no serviço `postgres`.

```powershell
$env:ConnectionStrings__Postgres = "Host=localhost;Port=5433;Database=library_loans;Username=library;Password=<senha do .env>"
```

## Criar uma migration

```powershell
dotnet ef migrations add <Nome> --project src/LibraryLoans.Infrastructure --startup-project src/LibraryLoans.Infrastructure --output-dir Migrations
```

## Aplicar no banco

```powershell
dotnet ef database update --project src/LibraryLoans.Infrastructure --startup-project src/LibraryLoans.Infrastructure
```

Em ambientes controlados, prefira gerar um script idempotente e aplicá-lo uma única vez:

```powershell
dotnet ef migrations script --idempotent --project src/LibraryLoans.Infrastructure --startup-project src/LibraryLoans.Infrastructure -o migrate.sql
```
