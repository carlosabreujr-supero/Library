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
    Task<Loan?> GetAsNoTrackingAsync(Guid id, CancellationToken cancellationToken);

    // UPDATE condicional (Status = 'Active'): só uma transação vence a transição. Retorna o BookId, ou null se não houve transição.
    Task<Guid?> TryCloseAsync(Guid id, LoanStatus newStatus, DateTime nowUtc, CancellationToken cancellationToken);
}

public interface IIdempotencyRepository
{
    // INSERT ... ON CONFLICT DO NOTHING. Se outra transação detém a mesma chave não confirmada,
    // o PostgreSQL bloqueia até o commit/rollback dela. True = esta transação passou a deter a chave.
    Task<bool> TryBeginAsync(string key, string requestHash, DateTime nowUtc, CancellationToken cancellationToken);
    Task<IdempotencyRecord?> GetAsync(string key, CancellationToken cancellationToken);
    Task CompleteAsync(string key, Guid loanId, int statusCode, string body, CancellationToken cancellationToken);
}

public interface ILoanQueries
{
    Task<PagedResult<Loan>> ListByReaderAsync(Guid readerId, LoanStatus? status, int page, int pageSize, CancellationToken cancellationToken);
    Task<PagedResult<Loan>> ListByBookAsync(Guid bookId, LoanStatus? status, int page, int pageSize, CancellationToken cancellationToken);
}
