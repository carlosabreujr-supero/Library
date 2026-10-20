using LibraryLoans.Application.Exceptions;

namespace LibraryLoans.Application.Books;

public sealed record BookAvailabilityDto(
    Guid BookId, bool IsActive, int TotalCopies, int AvailableCopies, int BorrowedCopies, bool IsAvailable);

public sealed record GetBookAvailabilityQuery(Guid Id);

public sealed class GetBookAvailabilityHandler(Abstractions.IBookRepository books)
{
    public async Task<BookAvailabilityDto> HandleAsync(GetBookAvailabilityQuery query, CancellationToken ct)
    {
        // Lê sempre do PostgreSQL (fonte autoritativa); sem cache nesta etapa.
        var book = await books.GetAsNoTrackingAsync(query.Id, ct)
            ?? throw new NotFoundException($"Livro {query.Id} não encontrado.");

        return new BookAvailabilityDto(
            book.Id, book.IsActive, book.TotalCopies, book.AvailableCopies,
            book.TotalCopies - book.AvailableCopies,
            IsAvailable: book.IsActive && book.AvailableCopies > 0);
    }
}
