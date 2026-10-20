using LibraryLoans.Application.Common;
using LibraryLoans.Domain.Entities;

namespace LibraryLoans.Application.Abstractions;

public interface IBookRepository
{
    Task<Book?> FindAsync(Guid id, CancellationToken cancellationToken);
    Task<Book?> GetAsNoTrackingAsync(Guid id, CancellationToken cancellationToken);
    Task<bool> ExistsAsync(Guid id, CancellationToken cancellationToken);
    void Add(Book book);
    Task<PagedResult<Book>> ListAsync(int page, int pageSize, string? search, bool includeInactive, CancellationToken cancellationToken);
}

// Registra o evento na mesma unidade de trabalho da alteração, garantindo atomicidade.
public interface IAuditTrail
{
    void Record(string entityType, Guid entityId, string action, object? data);
}

public interface IUnitOfWork
{
    Task SaveChangesAsync(CancellationToken cancellationToken);
}

public interface IRequestContext
{
    string CorrelationId { get; }
    string ActorId { get; }
}
