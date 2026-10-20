using LibraryLoans.Application.Audit;

namespace LibraryLoans.Api.Endpoints;

public static class AuditEndpoints
{
    public static IEndpointRouteBuilder MapAuditEndpoints(this IEndpointRouteBuilder app)
    {
        // Apenas GET: eventos de auditoria são imutáveis pela API.
        app.MapGet("/audit-events", async (
            ListAuditEventsHandler handler, CancellationToken ct,
            string? entityType = null, Guid? entityId = null, string? action = null, string? actorId = null,
            string? correlationId = null, DateTimeOffset? from = null, DateTimeOffset? to = null,
            int page = 1, int pageSize = 20) =>
            Results.Ok(await handler.HandleAsync(
                new ListAuditEventsQuery(entityType, entityId, action, actorId, correlationId, from, to, page, pageSize), ct)))
            .WithTags("Audit");

        return app;
    }
}
