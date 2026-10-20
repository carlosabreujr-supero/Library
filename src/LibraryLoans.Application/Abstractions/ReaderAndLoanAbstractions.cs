using LibraryLoans.Application.Common;
using LibraryLoans.Domain.Entities;

namespace LibraryLoans.Application.Abstractions;

public interface IReaderRepository
{
    void Add(Reader reader);
    Task<bool> ExistsAsync(Guid id, CancellationToken cancellationToken);
    Task<Reader?> GetAsNoTrackingAsync(Guid id, CancellationToken cancellationToken);
}

public interface ILoanRepository
{
    void Add(Loan loan);
}

public interface ILoanQueries
{
    Task<PagedResult<Loan>> ListByReaderAsync(Guid readerId, LoanStatus? status, int page, int pageSize, CancellationToken cancellationToken);
    Task<PagedResult<Loan>> ListByBookAsync(Guid bookId, LoanStatus? status, int page, int pageSize, CancellationToken cancellationToken);
}
