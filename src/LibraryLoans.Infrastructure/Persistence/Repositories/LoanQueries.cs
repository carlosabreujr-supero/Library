using LibraryLoans.Application.Abstractions;
using LibraryLoans.Application.Common;
using LibraryLoans.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace LibraryLoans.Infrastructure.Persistence.Repositories;

internal sealed class LoanQueries(LibraryDbContext db) : ILoanQueries
{
    public Task<PagedResult<Loan>> ListByReaderAsync(Guid readerId, LoanStatus? status, int page, int pageSize, CancellationToken ct) =>
        ListAsync(l => l.ReaderId == readerId, status, page, pageSize, ct);

    public Task<PagedResult<Loan>> ListByBookAsync(Guid bookId, LoanStatus? status, int page, int pageSize, CancellationToken ct) =>
        ListAsync(l => l.BookId == bookId, status, page, pageSize, ct);

    private async Task<PagedResult<Loan>> ListAsync(
        System.Linq.Expressions.Expression<Func<Loan, bool>> scope, LoanStatus? status, int page, int pageSize, CancellationToken ct)
    {
        var query = db.Loans.AsNoTracking().Where(scope);
        if (status is { } s)
            query = query.Where(l => l.Status == s);

        var total = await query.CountAsync(ct);
        var items = await query
            .OrderByDescending(l => l.BorrowedAtUtc).ThenBy(l => l.Id)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .ToListAsync(ct);

        return new PagedResult<Loan>(items, page, pageSize, total);
    }
}
