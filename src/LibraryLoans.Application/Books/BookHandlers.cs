using LibraryLoans.Application.Abstractions;
using LibraryLoans.Application.Audit;
using LibraryLoans.Application.Common;
using LibraryLoans.Application.Exceptions;
using LibraryLoans.Domain.Entities;

namespace LibraryLoans.Application.Books;

public sealed class CreateBookHandler(
    IBookRepository books, IAuditTrail audit, IUnitOfWork unitOfWork, IReadCache cache, TimeProvider clock)
{
    public async Task<BookDto> HandleAsync(CreateBookCommand command, CancellationToken ct)
    {
        var errors = new Dictionary<string, string[]>();
        BookValidation.Add(errors, "title", BookValidation.ValidateTitle(command.Title));
        BookValidation.Add(errors, "author", BookValidation.ValidateAuthor(command.Author));
        BookValidation.Add(errors, "isbn", BookValidation.ValidateIsbn(command.Isbn, out var isbn));
        BookValidation.Add(errors, "totalCopies", BookValidation.ValidateCopies(command.TotalCopies));
        BookValidation.ThrowIfAny(errors);

        var now = clock.GetUtcNow().UtcDateTime;
        var book = new Book
        {
            Id = Guid.NewGuid(),
            Title = command.Title!.Trim(),
            Author = command.Author!.Trim(),
            Isbn = isbn,
            TotalCopies = command.TotalCopies!.Value,
            AvailableCopies = command.TotalCopies.Value,
            IsActive = true,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };

        books.Add(book);
        audit.Record(nameof(Book), book.Id, AuditActions.BookCreated, new
        {
            book.Title, book.Author, book.Isbn, book.TotalCopies, book.AvailableCopies
        });
        await unitOfWork.SaveChangesAsync(ct);
        await cache.InvalidateAsync(CacheAreas.ForBook(book.Id));

        return BookDto.From(book);
    }
}

public sealed class UpdateBookHandler(
    IBookRepository books, IAuditTrail audit, IUnitOfWork unitOfWork, IReadCache cache, TimeProvider clock)
{
    public async Task<BookDto> HandleAsync(UpdateBookCommand command, CancellationToken ct)
    {
        if (command.Title is null && command.Author is null && command.Isbn is null && command.TotalCopies is null)
            throw new ValidationException("body", "Informe ao menos um campo para alterar.");

        var errors = new Dictionary<string, string[]>();
        var isbn = string.Empty;
        if (command.Title is not null) BookValidation.Add(errors, "title", BookValidation.ValidateTitle(command.Title));
        if (command.Author is not null) BookValidation.Add(errors, "author", BookValidation.ValidateAuthor(command.Author));
        if (command.Isbn is not null) BookValidation.Add(errors, "isbn", BookValidation.ValidateIsbn(command.Isbn, out isbn));
        if (command.TotalCopies is not null) BookValidation.Add(errors, "totalCopies", BookValidation.ValidateCopies(command.TotalCopies));
        BookValidation.ThrowIfAny(errors);

        var book = await books.FindAsync(command.Id, ct)
            ?? throw new NotFoundException($"Livro {command.Id} não encontrado.");

        if (!book.IsActive)
            throw new ConflictException("Livro desativado não pode ser alterado.");

        var changes = new Dictionary<string, object>();

        if (command.Title is not null && command.Title.Trim() != book.Title)
        {
            changes["title"] = new { from = book.Title, to = command.Title.Trim() };
            book.Title = command.Title.Trim();
        }
        if (command.Author is not null && command.Author.Trim() != book.Author)
        {
            changes["author"] = new { from = book.Author, to = command.Author.Trim() };
            book.Author = command.Author.Trim();
        }
        if (command.Isbn is not null && isbn != book.Isbn)
        {
            changes["isbn"] = new { from = book.Isbn, to = isbn };
            book.Isbn = isbn;
        }
        if (command.TotalCopies is { } newTotal && newTotal != book.TotalCopies)
        {
            // Exemplares emprestados = Total - Disponíveis; o novo total não pode ficar abaixo disso.
            var borrowed = book.TotalCopies - book.AvailableCopies;
            if (newTotal < borrowed)
                throw new ConflictException(
                    $"TotalCopies ({newTotal}) não pode ser menor que a quantidade emprestada ({borrowed}).");

            changes["totalCopies"] = new { from = book.TotalCopies, to = newTotal };
            changes["availableCopies"] = new { from = book.AvailableCopies, to = newTotal - borrowed };
            book.TotalCopies = newTotal;
            book.AvailableCopies = newTotal - borrowed;
        }

        if (changes.Count == 0)
            return BookDto.From(book);

        book.UpdatedAtUtc = clock.GetUtcNow().UtcDateTime;
        audit.Record(nameof(Book), book.Id, AuditActions.BookUpdated, changes);
        await unitOfWork.SaveChangesAsync(ct);
        await cache.InvalidateAsync(CacheAreas.ForBook(book.Id));

        return BookDto.From(book);
    }
}

public sealed class DeactivateBookHandler(
    IBookRepository books, IAuditTrail audit, IUnitOfWork unitOfWork, IReadCache cache, TimeProvider clock)
{
    public async Task HandleAsync(DeactivateBookCommand command, CancellationToken ct)
    {
        var book = await books.FindAsync(command.Id, ct)
            ?? throw new NotFoundException($"Livro {command.Id} não encontrado.");

        // Idempotente: desativar de novo não gera novo evento de auditoria.
        if (!book.IsActive)
            return;

        book.IsActive = false;
        book.UpdatedAtUtc = clock.GetUtcNow().UtcDateTime;
        audit.Record(nameof(Book), book.Id, AuditActions.BookDeactivated, new { book.Title, book.Isbn });
        await unitOfWork.SaveChangesAsync(ct);
        await cache.InvalidateAsync(CacheAreas.ForBook(book.Id));
    }
}

public sealed class GetBookHandler(IBookRepository books)
{
    public async Task<BookDto> HandleAsync(GetBookQuery query, CancellationToken ct)
    {
        var book = await books.FindAsync(query.Id, ct)
            ?? throw new NotFoundException($"Livro {query.Id} não encontrado.");
        return BookDto.From(book);
    }
}

public sealed class ListBooksHandler(IBookRepository books, IReadCache cache)
{
    public const int MaxPageSize = 100;

    public async Task<PagedResult<BookDto>> HandleAsync(ListBooksQuery query, CancellationToken ct)
    {
        var errors = new Dictionary<string, string[]>();
        if (query.Page < 1) errors["page"] = ["page deve ser maior ou igual a 1."];
        if (query.PageSize is < 1 or > MaxPageSize) errors["pageSize"] = [$"pageSize deve estar entre 1 e {MaxPageSize}."];
        BookValidation.ThrowIfAny(errors);

        var search = query.Search?.Trim();
        var key = ListKey(query.Page, query.PageSize, search, query.IncludeInactive);

        return await cache.GetOrCreateAsync(CacheAreas.BookList, key, async token =>
        {
            var result = await books.ListAsync(query.Page, query.PageSize, search, query.IncludeInactive, token);
            return new PagedResult<BookDto>(result.Items.Select(BookDto.From).ToList(), result.Page, result.PageSize, result.TotalCount);
        }, ct);
    }

    // Busca em hash: evita caracteres especiais e tamanho ilimitado na chave do Redis.
    private static string ListKey(int page, int pageSize, string? search, bool includeInactive) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes($"{page}|{pageSize}|{includeInactive}|{search}")));
}
