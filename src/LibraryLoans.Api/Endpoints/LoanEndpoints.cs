using LibraryLoans.Application.Loans;

namespace LibraryLoans.Api.Endpoints;

[System.Text.Json.Serialization.JsonUnmappedMemberHandling(System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow)]
public sealed record CreateLoanRequest(Guid? BookId, Guid? ReaderId);

public static class LoanEndpoints
{
    public const string IdempotencyKeyHeader = "Idempotency-Key";
    public const string ReplayedHeader = "Idempotent-Replayed";

    public static IEndpointRouteBuilder MapLoanEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/loans").WithTags("Loans");

        group.MapPost("/", async (
            CreateLoanRequest request, HttpContext http, CreateLoanHandler handler, CancellationToken ct) =>
        {
            var key = http.Request.Headers[IdempotencyKeyHeader].ToString();
            var result = await handler.HandleAsync(new CreateLoanCommand(request.BookId, request.ReaderId, key), ct);

            if (result.Replayed)
                http.Response.Headers[ReplayedHeader] = "true";

            return Results.Content(result.Body, "application/json", statusCode: result.StatusCode);
        });

        return app;
    }
}
