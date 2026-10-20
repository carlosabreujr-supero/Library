namespace LibraryLoans.Domain.Entities;

public class AuditEvent
{
    public Guid Id { get; set; }
    public string EntityType { get; set; } = string.Empty;
    public Guid EntityId { get; set; }
    public string Action { get; set; } = string.Empty;
    public string? ActorId { get; set; }
    public DateTime TimestampUtc { get; set; }
    public string? CorrelationId { get; set; }
    // JSON serializado, persistido como jsonb.
    public string? Data { get; set; }
}
