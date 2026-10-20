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
}
