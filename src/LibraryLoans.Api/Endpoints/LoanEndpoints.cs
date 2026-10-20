using LibraryLoans.Application.Loans;

namespace LibraryLoans.Api.Endpoints;

[System.Text.Json.Serialization.JsonUnmappedMemberHandling(System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow)]
public sealed record CreateLoanRequest(Guid? BookId, Guid? ReaderId);

public static class LoanEndpoints
{
    public static IEndpointRouteBuilder MapLoanEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/loans").WithTags("Loans");

        group.MapPost("/", async (CreateLoanRequest request, CreateLoanHandler handler, CancellationToken ct) =>
        {
            var loan = await handler.HandleAsync(new CreateLoanCommand(request.BookId, request.ReaderId), ct);
            return Results.Json(loan, statusCode: StatusCodes.Status201Created);
        });

        return app;
    }
}
