using System.Text.Json.Serialization;
using LibraryLoans.Application.Books;
using LibraryLoans.Application.Common;

namespace LibraryLoans.Api.Endpoints;

// Campos desconhecidos (ex.: availableCopies) são rejeitados com 400.
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CreateBookRequest(string? Title, string? Author, string? Isbn, int? TotalCopies);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record UpdateBookRequest(string? Title, string? Author, string? Isbn, int? TotalCopies);

public static class BookEndpoints
{
    public static IEndpointRouteBuilder MapBookEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/books").WithTags("Books");

        group.MapPost("/", async (CreateBookRequest request, CreateBookHandler handler, CancellationToken ct) =>
        {
            var book = await handler.HandleAsync(
                new CreateBookCommand(request.Title, request.Author, request.Isbn, request.TotalCopies), ct);
            return Results.Created($"/books/{book.Id}", book);
        });

        group.MapGet("/", async (
            ListBooksHandler handler, CancellationToken ct,
            int page = 1, int pageSize = 20, string? search = null, bool includeInactive = false) =>
            Results.Ok(await handler.HandleAsync(new ListBooksQuery(page, pageSize, search, includeInactive), ct)));

        group.MapGet("/{id:guid}", async (Guid id, GetBookHandler handler, CancellationToken ct) =>
            Results.Ok(await handler.HandleAsync(new GetBookQuery(id), ct)));

        group.MapPatch("/{id:guid}", async (Guid id, UpdateBookRequest request, UpdateBookHandler handler, CancellationToken ct) =>
            Results.Ok(await handler.HandleAsync(
                new UpdateBookCommand(id, request.Title, request.Author, request.Isbn, request.TotalCopies), ct)));

        group.MapDelete("/{id:guid}", async (Guid id, DeactivateBookHandler handler, CancellationToken ct) =>
        {
            await handler.HandleAsync(new DeactivateBookCommand(id), ct);
            return Results.NoContent();
        });

        return app;
    }
}
