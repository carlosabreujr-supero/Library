using LibraryLoans.Infrastructure.Caching;
using LibraryLoans.Infrastructure.Persistence;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace LibraryLoans.Infrastructure.Health;

public sealed class HealthSettings
{
    // O Redis é só cache de leitura e há fallback para o PostgreSQL; por padrão sua queda degrada a API
    // (readiness continua 200) em vez de tirá-la do balanceador. Defina true para exigi-lo.
    public bool RedisRequired { get; set; }
    public int TimeoutMs { get; set; } = 2000;
}

public sealed class PostgresHealthCheck(LibraryDbContext db, HealthSettings settings) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(settings.TimeoutMs);
        try
        {
            return await db.Database.CanConnectAsync(timeout.Token)
                ? HealthCheckResult.Healthy("PostgreSQL acessível.")
                : HealthCheckResult.Unhealthy("PostgreSQL inacessível.");
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            // Sem a exceção no resultado: evita vazar host/credenciais na resposta.
            return HealthCheckResult.Unhealthy("PostgreSQL inacessível.");
        }
    }
}

public sealed class RedisHealthCheck(
    IServiceProvider services, CacheSettings cacheSettings, HealthSettings settings) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        if (!cacheSettings.Enabled)
            return HealthCheckResult.Healthy("Cache desabilitado.");

        var failure = settings.RedisRequired
            ? HealthCheckResult.Unhealthy("Redis inacessível.")
            : HealthCheckResult.Degraded("Redis inacessível; usando PostgreSQL como fallback.");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(settings.TimeoutMs);
        try
        {
            var cache = (IDistributedCache?)services.GetService(typeof(IDistributedCache));
            if (cache is null) return failure;

            await cache.GetStringAsync($"{cacheSettings.KeyPrefix}:health", timeout.Token);
            return HealthCheckResult.Healthy("Redis acessível.");
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return failure;
        }
    }
}
