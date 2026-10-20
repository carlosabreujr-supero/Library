using LibraryLoans.Application.Abstractions;
using LibraryLoans.Application.Common;
using LibraryLoans.Domain.Entities;

namespace LibraryLoans.UnitTests;

internal sealed class FakeBooks : IBookRepository
{
    public Dictionary<Guid, Book> Items { get; } = [];

    public Book Seed(int total = 1, int? available = null, bool active = true)
    {
        var b = new Book
        {
            Id = Guid.NewGuid(), Title = "T", Author = "A", Isbn = "9780306406157",
            TotalCopies = total, AvailableCopies = available ?? total, IsActive = active
        };
        Items[b.Id] = b;
        return b;
    }

    public Task<Book?> FindAsync(Guid id, CancellationToken ct) => Task.FromResult(Items.GetValueOrDefault(id));
    public Task<Book?> GetAsNoTrackingAsync(Guid id, CancellationToken ct) => FindAsync(id, ct);
    public Task<bool> ExistsAsync(Guid id, CancellationToken ct) => Task.FromResult(Items.ContainsKey(id));
    public void Add(Book book) => Items[book.Id] = book;

    public Task<bool> TryReserveCopyAsync(Guid id, DateTime nowUtc, CancellationToken ct)
    {
        if (Items.TryGetValue(id, out var b) && b.IsActive && b.AvailableCopies > 0)
        {
            b.AvailableCopies--;
            return Task.FromResult(true);
        }
        return Task.FromResult(false);
    }

    public Task<bool> ReleaseCopyAsync(Guid id, DateTime nowUtc, CancellationToken ct)
    {
        if (Items.TryGetValue(id, out var b) && b.AvailableCopies < b.TotalCopies)
        {
            b.AvailableCopies++;
            return Task.FromResult(true);
        }
        return Task.FromResult(false);
    }

    public Task<PagedResult<Book>> ListAsync(int page, int pageSize, string? search, bool includeInactive, CancellationToken ct) =>
        throw new NotSupportedException();
}

internal sealed class FakeReaders : IReaderRepository
{
    public Dictionary<Guid, Reader> Items { get; } = [];

    public Reader Seed(bool active = true)
    {
        var r = new Reader { Id = Guid.NewGuid(), Name = "R", IsActive = active };
        Items[r.Id] = r;
        return r;
    }

    public void Add(Reader reader) => Items[reader.Id] = reader;
    public Task<bool> ExistsAsync(Guid id, CancellationToken ct) => Task.FromResult(Items.ContainsKey(id));
    public Task<Reader?> GetAsNoTrackingAsync(Guid id, CancellationToken ct) => Task.FromResult(Items.GetValueOrDefault(id));
}

internal sealed class FakeLoans : ILoanRepository
{
    public Dictionary<Guid, Loan> Items { get; } = [];

    public Loan Seed(Guid bookId, LoanStatus status = LoanStatus.Active)
    {
        var l = new Loan { Id = Guid.NewGuid(), BookId = bookId, ReaderId = Guid.NewGuid(), Status = status };
        Items[l.Id] = l;
        return l;
    }

    public void Add(Loan loan) => Items[loan.Id] = loan;
    public Task<Loan?> GetAsNoTrackingAsync(Guid id, CancellationToken ct) => Task.FromResult(Items.GetValueOrDefault(id));

    public Task<Guid?> TryCloseAsync(Guid id, LoanStatus newStatus, DateTime nowUtc, CancellationToken ct)
    {
        if (!Items.TryGetValue(id, out var l) || l.Status != LoanStatus.Active) return Task.FromResult<Guid?>(null);
        l.Status = newStatus;
        if (newStatus == LoanStatus.Returned) l.ReturnedAtUtc = nowUtc; else l.CancelledAtUtc = nowUtc;
        return Task.FromResult<Guid?>(l.BookId);
    }
}

internal sealed class FakeIdempotency : IIdempotencyRepository
{
    public Dictionary<string, IdempotencyRecord> Items { get; } = [];

    public Task<bool> TryBeginAsync(string key, string requestHash, DateTime nowUtc, CancellationToken ct)
    {
        if (Items.ContainsKey(key)) return Task.FromResult(false);
        Items[key] = new IdempotencyRecord { Key = key, RequestHash = requestHash, Status = IdempotencyStatus.InProgress };
        return Task.FromResult(true);
    }

    public Task<IdempotencyRecord?> GetAsync(string key, CancellationToken ct) => Task.FromResult(Items.GetValueOrDefault(key));

    public Task CompleteAsync(string key, Guid loanId, int statusCode, string body, CancellationToken ct)
    {
        var r = Items[key];
        r.Status = IdempotencyStatus.Completed;
        r.LoanId = loanId;
        r.ResponseStatusCode = statusCode;
        r.ResponseBody = body;
        return Task.CompletedTask;
    }
}

internal sealed class FakeAudit : IAuditTrail
{
    public List<(string Type, Guid Id, string Action)> Events { get; } = [];
    public void Record(string entityType, Guid entityId, string action, object? data) => Events.Add((entityType, entityId, action));
}

internal sealed class FakeUnitOfWork : IUnitOfWork
{
    public Task SaveChangesAsync(CancellationToken ct) => Task.CompletedTask;
    public Task<T> ExecuteInTransactionAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken ct) => operation(ct);
}
