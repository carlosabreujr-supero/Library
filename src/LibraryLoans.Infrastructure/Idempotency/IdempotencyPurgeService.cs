using LibraryLoans.Application.Loans;
using LibraryLoans.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LibraryLoans.Infrastructure.Idempotency;

// Remove chaves expiradas. O DELETE é idempotente, então várias réplicas executando-o ao mesmo tempo é seguro.
internal sealed class IdempotencyPurgeService(
    IServiceScopeFactory scopeFactory,
    IdempotencySettings settings,
    TimeProvider clock,
    ILogger<IdempotencyPurgeService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(settings.PurgeIntervalMinutes));
        do
        {
            try
            {
                await PurgeAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Falha ao remover chaves de idempotência expiradas.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task PurgeAsync(CancellationToken ct)
    {
        var cutoff = clock.GetUtcNow().UtcDateTime.AddHours(-settings.RetentionHours);
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LibraryDbContext>();
        var removed = await db.IdempotencyRecords.Where(i => i.CreatedAtUtc < cutoff).ExecuteDeleteAsync(ct);
        if (removed > 0)
            logger.LogInformation("Removidas {Count} chaves de idempotência expiradas.", removed);
    }
}
