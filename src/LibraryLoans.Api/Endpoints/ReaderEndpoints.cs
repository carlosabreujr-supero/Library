using LibraryLoans.Application.Books;
using LibraryLoans.Application.Loans;
using LibraryLoans.Application.Readers;

namespace LibraryLoans.Api.Endpoints;

[System.Text.Json.Serialization.JsonUnmappedMemberHandling(System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow)]
public sealed record CreateReaderRequest(string? Name);

public static class ReaderEndpoints
{
    public static IEndpointRouteBuilder MapReaderEndpoints(this IEndpointRouteBuilder app)
    {
        var users = app.MapGroup("/users").WithTags("Users");

        users.MapPost("/", async (CreateReaderRequest request, CreateReaderHandler handler, CancellationToken ct) =>
        {
            var reader = await handler.HandleAsync(new CreateReaderCommand(request.Name), ct);
            return Results.Created($"/users/{reader.Id}", reader);
        });

        users.MapGet("/{id:guid}/loans", async (
            Guid id, GetReaderLoansHandler handler, CancellationToken ct,
            int page = 1, int pageSize = 20, string? status = null) =>
            Results.Ok(await handler.HandleAsync(new LoanHistoryQuery(id, page, pageSize, status), ct)));

        var books = app.MapGroup("/books").WithTags("Books");

        books.MapGet("/{id:guid}/history", async (
            Guid id, GetBookHistoryHandler handler, CancellationToken ct,
            int page = 1, int pageSize = 20, string? status = null) =>
            Results.Ok(await handler.HandleAsync(new LoanHistoryQuery(id, page, pageSize, status), ct)));

        books.MapGet("/{id:guid}/availability", async (Guid id, GetBookAvailabilityHandler handler, CancellationToken ct) =>
            Results.Ok(await handler.HandleAsync(new GetBookAvailabilityQuery(id), ct)));

        return app;
    }
}
