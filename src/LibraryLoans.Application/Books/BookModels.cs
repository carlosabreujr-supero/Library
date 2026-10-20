using LibraryLoans.Domain.Entities;

namespace LibraryLoans.Application.Books;

public sealed record BookDto(
    Guid Id,
    string Title,
    string Isbn,
    string Author,
    int TotalCopies,
    int AvailableCopies,
    bool IsActive,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc)
{
    public static BookDto From(Book b) => new(
        b.Id, b.Title, b.Isbn, b.Author, b.TotalCopies, b.AvailableCopies, b.IsActive, b.CreatedAtUtc, b.UpdatedAtUtc);
}

public sealed record CreateBookCommand(string? Title, string? Author, string? Isbn, int? TotalCopies);

// Campos nulos significam "não alterar". AvailableCopies não é aceito: é derivado dos empréstimos.
public sealed record UpdateBookCommand(Guid Id, string? Title, string? Author, string? Isbn, int? TotalCopies);

public sealed record DeactivateBookCommand(Guid Id);

public sealed record GetBookQuery(Guid Id);

public sealed record ListBooksQuery(int Page = 1, int PageSize = 20, string? Search = null, bool IncludeInactive = false);
