using LibraryLoans.Application.Abstractions;
using LibraryLoans.Application.Exceptions;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace LibraryLoans.Infrastructure.Persistence;

internal sealed class UnitOfWork(LibraryDbContext db) : IUnitOfWork
{
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
