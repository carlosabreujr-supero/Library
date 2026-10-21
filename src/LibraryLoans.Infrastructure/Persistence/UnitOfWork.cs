using LibraryLoans.Application.Abstractions;
using LibraryLoans.Application.Exceptions;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace LibraryLoans.Infrastructure.Persistence;

internal sealed class UnitOfWork(LibraryDbContext db) : IUnitOfWork
{
    public async Task<T> ExecuteInTransactionAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken)
    {
        // A estratégia de retry repete a unidade inteira (transação + operação) em falhas transitórias de conexão.
        // O estado rastreado de uma tentativa abortada é descartado; a operação é idempotente por construção
        // (chave de idempotência / UPDATE condicional), então a repetição não duplica efeitos.
        var strategy = db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async ct =>
        {
            db.ChangeTracker.Clear();
            // O dispose sem commit (por exceção) reverte a transação.
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            var result = await operation(ct);
            await transaction.CommitAsync(ct);
            return result;
        }, cancellationToken);
    }
    public async Task SaveChangesAsync(CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new ConflictException("O recurso foi modificado por outra requisição. Tente novamente.");
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException pg)
        {
            throw pg.SqlState switch
            {
                PostgresErrorCodes.UniqueViolation when pg.ConstraintName == "ux_books_isbn"
                    => new ConflictException("ISBN já cadastrado."),
                PostgresErrorCodes.UniqueViolation => new ConflictException("Registro duplicado."),
                PostgresErrorCodes.CheckViolation => new ConflictException("A operação viola uma regra de integridade."),
                _ => ex
            };
        }
    }
}
