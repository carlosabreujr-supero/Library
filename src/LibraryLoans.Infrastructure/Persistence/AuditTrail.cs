using System.Text.Json;
using LibraryLoans.Application.Abstractions;
using LibraryLoans.Domain.Entities;

namespace LibraryLoans.Infrastructure.Persistence;

internal sealed class AuditTrail(LibraryDbContext db, IRequestContext context, TimeProvider clock) : IAuditTrail
{
    public void Record(string entityType, Guid entityId, string action, object? data)
    {
        db.AuditEvents.Add(new AuditEvent
        {
            Id = Guid.NewGuid(),
            EntityType = entityType,
            EntityId = entityId,
            Action = action,
            ActorId = context.ActorId,
            CorrelationId = context.CorrelationId,
            TimestampUtc = clock.GetUtcNow().UtcDateTime,
            Data = data is null ? null : JsonSerializer.Serialize(data)
        });
    }
}
