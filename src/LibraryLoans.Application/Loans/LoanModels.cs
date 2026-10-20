using LibraryLoans.Domain.Entities;

namespace LibraryLoans.Application.Loans;

public sealed record LoanDto(
    Guid Id,
    Guid BookId,
    Guid ReaderId,
    string Status,
    DateTime BorrowedAtUtc,
    DateTime DueAtUtc,
    DateTime? ReturnedAtUtc,
    DateTime? CancelledAtUtc)
{
    public static LoanDto From(Loan l) => new(
        l.Id, l.BookId, l.ReaderId, l.Status.ToString(), l.BorrowedAtUtc, l.DueAtUtc, l.ReturnedAtUtc, l.CancelledAtUtc);
}

public sealed record LoanHistoryQuery(Guid Id, int Page = 1, int PageSize = 20, string? Status = null);
