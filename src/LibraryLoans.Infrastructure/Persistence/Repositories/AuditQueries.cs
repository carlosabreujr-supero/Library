using LibraryLoans.Application.Abstractions;
using LibraryLoans.Application.Audit;
using LibraryLoans.Application.Common;
using LibraryLoans.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace LibraryLoans.Infrastructure.Persistence.Repositories;

// Somente leitura: a API não expõe edição nem exclusão de eventos (e o banco também as bloqueia por trigger).
internal sealed class AuditQueries(LibraryDbContext db) : IAuditQueries
{
    public async Task<PagedResult<AuditEvent>> ListAsync(AuditEventFilter filter, int page, int pageSize, CancellationToken ct)
    {
        var query = db.AuditEvents.AsNoTracking();

        if (filter.EntityType is not null) query = query.Where(a => a.EntityType == filter.EntityType);
        if (filter.EntityId is { } id) query = query.Where(a => a.EntityId == id);
        if (filter.Action is not null) query = query.Where(a => a.Action == filter.Action);
        if (filter.ActorId is not null) query = query.Where(a => a.ActorId == filter.ActorId);
        if (filter.CorrelationId is not null) query = query.Where(a => a.CorrelationId == filter.CorrelationId);
        if (filter.FromUtc is { } from) query = query.Where(a => a.TimestampUtc >= from);
        if (filter.ToUtc is { } to) query = query.Where(a => a.TimestampUtc <= to);

        var total = await query.CountAsync(ct);
        var items = await query
            .OrderByDescending(a => a.TimestampUtc).ThenBy(a => a.Id)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .ToListAsync(ct);

        return new PagedResult<AuditEvent>(items, page, pageSize, total);
    }
}
