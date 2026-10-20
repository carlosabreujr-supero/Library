using LibraryLoans.Application.Abstractions;
using LibraryLoans.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace LibraryLoans.Infrastructure.Persistence.Repositories;

internal sealed class IdempotencyRepository(LibraryDbContext db) : IIdempotencyRepository
{
    public async Task<bool> TryBeginAsync(string key, string requestHash, DateTime nowUtc, CancellationToken cancellationToken)
    {
        var inserted = await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO idempotency_records ("Key", "RequestHash", "Status", "CreatedAtUtc")
            VALUES ({key}, {requestHash}, 'InProgress', {nowUtc})
            ON CONFLICT ("Key") DO NOTHING
            """, cancellationToken);
        return inserted == 1;
    }

    public Task<IdempotencyRecord?> GetAsync(string key, CancellationToken cancellationToken) =>
        db.IdempotencyRecords.AsNoTracking().FirstOrDefaultAsync(i => i.Key == key, cancellationToken);

    public async Task CompleteAsync(string key, Guid loanId, int statusCode, string body, CancellationToken cancellationToken)
    {
        var updated = await db.IdempotencyRecords.Where(i => i.Key == key).ExecuteUpdateAsync(s => s
            .SetProperty(i => i.Status, IdempotencyStatus.Completed)
            .SetProperty(i => i.LoanId, loanId)
            .SetProperty(i => i.ResponseStatusCode, statusCode)
            .SetProperty(i => i.ResponseBody, body), cancellationToken);

        if (updated != 1)
            throw new InvalidOperationException("Registro de idempotência não encontrado ao concluir.");
    }
}
