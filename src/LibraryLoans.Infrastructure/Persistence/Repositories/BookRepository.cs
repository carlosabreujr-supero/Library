using LibraryLoans.Application.Abstractions;
using LibraryLoans.Application.Common;
using LibraryLoans.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace LibraryLoans.Infrastructure.Persistence.Repositories;

internal sealed class BookRepository(LibraryDbContext db) : IBookRepository
{
    public Task<Book?> FindAsync(Guid id, CancellationToken cancellationToken) =>
        db.Books.FirstOrDefaultAsync(b => b.Id == id, cancellationToken);

    public void Add(Book book) => db.Books.Add(book);

    public Task<Book?> GetAsNoTrackingAsync(Guid id, CancellationToken cancellationToken) =>
        db.Books.AsNoTracking().FirstOrDefaultAsync(b => b.Id == id, cancellationToken);

    public Task<bool> ExistsAsync(Guid id, CancellationToken cancellationToken) =>
        db.Books.AnyAsync(b => b.Id == id, cancellationToken);

    public async Task<bool> TryReserveCopyAsync(Guid id, DateTime nowUtc, CancellationToken cancellationToken)
    {
        var affected = await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE books
            SET "AvailableCopies" = "AvailableCopies" - 1, "UpdatedAtUtc" = {nowUtc}
            WHERE "Id" = {id} AND "IsActive" = TRUE AND "AvailableCopies" > 0
            """, cancellationToken);
        return affected == 1;
    }

    public async Task<bool> ReleaseCopyAsync(Guid id, DateTime nowUtc, CancellationToken cancellationToken)
    {
        var affected = await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE books
            SET "AvailableCopies" = "AvailableCopies" + 1, "UpdatedAtUtc" = {nowUtc}
            WHERE "Id" = {id} AND "AvailableCopies" < "TotalCopies"
            """, cancellationToken);
        return affected == 1;
    }

    public async Task<PagedResult<Book>> ListAsync(
        int page, int pageSize, string? search, bool includeInactive, CancellationToken cancellationToken)
    {
        var query = db.Books.AsNoTracking();

        if (!includeInactive)
            query = query.Where(b => b.IsActive);

        if (!string.IsNullOrEmpty(search))
        {
            var pattern = $"%{search.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_")}%";
            query = query.Where(b =>
                EF.Functions.ILike(b.Title, pattern) ||
                EF.Functions.ILike(b.Author, pattern) ||
                EF.Functions.ILike(b.Isbn, pattern));
        }

        var total = await query.CountAsync(cancellationToken);
        var items = await query
            .OrderBy(b => b.Title).ThenBy(b => b.Id)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .ToListAsync(cancellationToken);

        return new PagedResult<Book>(items, page, pageSize, total);
    }
}
