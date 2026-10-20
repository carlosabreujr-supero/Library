using System.Text.Json;
using LibraryLoans.Application.Abstractions;
using LibraryLoans.Application.Common;
using LibraryLoans.Application.Exceptions;
using LibraryLoans.Domain.Entities;

namespace LibraryLoans.Application.Audit;

// Nomes de ação persistidos; LoanReturned e LoanCancelled ficam reservados para os casos de uso de devolução/cancelamento.
public static class AuditActions
{
    public const string BookCreated = "BookCreated";
    public const string BookUpdated = "BookUpdated";
    public const string BookDeactivated = "BookDeactivated";
    public const string LoanCreated = "LoanCreated";
    public const string LoanReturned = "LoanReturned";
    public const string LoanCancelled = "LoanCancelled";
}

public sealed record AuditEventDto(
    Guid Id,
    string EntityType,
    Guid EntityId,
    string Action,
    string? ActorId,
    DateTime TimestampUtc,
    string? CorrelationId,
    JsonElement? Data)
{
    public static AuditEventDto From(AuditEvent e) => new(
        e.Id, e.EntityType, e.EntityId, e.Action, e.ActorId, e.TimestampUtc, e.CorrelationId,
        e.Data is null ? null : JsonDocument.Parse(e.Data).RootElement.Clone());
}

public sealed record AuditEventFilter(
    string? EntityType, Guid? EntityId, string? Action, string? ActorId, string? CorrelationId,
    DateTime? FromUtc, DateTime? ToUtc);

public sealed record ListAuditEventsQuery(
    string? EntityType = null, Guid? EntityId = null, string? Action = null, string? ActorId = null,
    string? CorrelationId = null, DateTimeOffset? From = null, DateTimeOffset? To = null,
    int Page = 1, int PageSize = 20);

public sealed class ListAuditEventsHandler(IAuditQueries audit)
{
    public const int MaxPageSize = 100;

    public async Task<PagedResult<AuditEventDto>> HandleAsync(ListAuditEventsQuery query, CancellationToken ct)
    {
        var errors = new Dictionary<string, string[]>();
        if (query.Page < 1) errors["page"] = ["page deve ser maior ou igual a 1."];
        if (query.PageSize is < 1 or > MaxPageSize) errors["pageSize"] = [$"pageSize deve estar entre 1 e {MaxPageSize}."];
        if (query.From is { } f && query.To is { } t && f > t) errors["from"] = ["from não pode ser posterior a to."];
        if (errors.Count > 0) throw new ValidationException(errors);

        var filter = new AuditEventFilter(
            Normalize(query.EntityType), query.EntityId, Normalize(query.Action), Normalize(query.ActorId),
            Normalize(query.CorrelationId), query.From?.UtcDateTime, query.To?.UtcDateTime);

        var result = await audit.ListAsync(filter, query.Page, query.PageSize, ct);
        return new PagedResult<AuditEventDto>(result.Items.Select(AuditEventDto.From).ToList(), result.Page, result.PageSize, result.TotalCount);
    }

    private static string? Normalize(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
