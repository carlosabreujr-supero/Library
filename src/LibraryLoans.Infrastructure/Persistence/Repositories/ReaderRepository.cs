using LibraryLoans.Application.Abstractions;
using LibraryLoans.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace LibraryLoans.Infrastructure.Persistence.Repositories;

internal sealed class ReaderRepository(LibraryDbContext db) : IReaderRepository
{
    public void Add(Reader reader) => db.Readers.Add(reader);

    public Task<bool> ExistsAsync(Guid id, CancellationToken cancellationToken) =>
        db.Readers.AnyAsync(r => r.Id == id, cancellationToken);

    public Task<Reader?> GetAsNoTrackingAsync(Guid id, CancellationToken cancellationToken) =>
        db.Readers.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id, cancellationToken);
}

internal sealed class LoanRepository(LibraryDbContext db) : ILoanRepository
{
    public void Add(Loan loan) => db.Loans.Add(loan);

    public Task<Loan?> GetAsNoTrackingAsync(Guid id, CancellationToken cancellationToken) =>
        db.Loans.AsNoTracking().FirstOrDefaultAsync(l => l.Id == id, cancellationToken);

    public async Task<Guid?> TryCloseAsync(Guid id, LoanStatus newStatus, DateTime nowUtc, CancellationToken cancellationToken)
    {
        var active = LoanStatus.Active.ToString();
        var target = newStatus.ToString();
        var returnedAt = newStatus == LoanStatus.Returned ? nowUtc : (DateTime?)null;
        var cancelledAt = newStatus == LoanStatus.Cancelled ? nowUtc : (DateTime?)null;

        var result = await db.Database.SqlQuery<Guid>($"""
            UPDATE loans
            SET "Status" = {target}, "ReturnedAtUtc" = {returnedAt}, "CancelledAtUtc" = {cancelledAt}
            WHERE "Id" = {id} AND "Status" = {active}
            RETURNING "BookId" AS "Value"
            """).ToListAsync(cancellationToken);
        return result.Count == 1 ? result[0] : null;
    }
}
