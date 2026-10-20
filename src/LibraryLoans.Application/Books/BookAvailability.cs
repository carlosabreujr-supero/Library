using LibraryLoans.Application.Exceptions;

namespace LibraryLoans.Application.Books;

public sealed record BookAvailabilityDto(
    Guid BookId, bool IsActive, int TotalCopies, int AvailableCopies, int BorrowedCopies, bool IsAvailable);

public sealed record GetBookAvailabilityQuery(Guid Id);

public sealed class GetBookAvailabilityHandler(Abstractions.IBookRepository books, Abstractions.IReadCache cache)
{
    public Task<BookAvailabilityDto> HandleAsync(GetBookAvailabilityQuery query, CancellationToken ct) =>
        // Consulta informativa: pode vir do cache (TTL curto, invalidado por empréstimos e alterações).
        // A decisão de empréstimo nunca usa este caminho; ela é feita por UPDATE condicional no PostgreSQL.
        cache.GetOrCreateAsync(Abstractions.CacheAreas.Availability(query.Id), "data", async token =>
        {
            var book = await books.GetAsNoTrackingAsync(query.Id, token)
                ?? throw new NotFoundException($"Livro {query.Id} não encontrado.");

            return new BookAvailabilityDto(
                book.Id, book.IsActive, book.TotalCopies, book.AvailableCopies,
                book.TotalCopies - book.AvailableCopies,
                IsAvailable: book.IsActive && book.AvailableCopies > 0);
        }, ct);
}
