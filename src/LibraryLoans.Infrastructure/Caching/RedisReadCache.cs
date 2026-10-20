using System.Diagnostics.Metrics;
using System.Text.Json;
using LibraryLoans.Application.Abstractions;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging;

namespace LibraryLoans.Infrastructure.Caching;

public sealed class CacheSettings
{
    public const int VersionTtlHours = 24;

    public bool Enabled { get; set; } = true;
    public int TtlSeconds { get; set; } = 30;
    public string KeyPrefix { get; set; } = "library:v1";
    // Após uma falha, o cache é ignorado por este período (evita pagar o timeout em toda requisição).
    public int CircuitBreakerSeconds { get; set; } = 10;
    public int TimeoutMs { get; set; } = 500;

    public void Validate()
    {
        if (TtlSeconds is < 1 or > 3600) throw new InvalidOperationException("Cache:TtlSeconds deve estar entre 1 e 3600.");
        if (CircuitBreakerSeconds < 0) throw new InvalidOperationException("Cache:CircuitBreakerSeconds deve ser >= 0.");
        if (TimeoutMs < 50) throw new InvalidOperationException("Cache:TimeoutMs deve ser >= 50.");
        if (string.IsNullOrWhiteSpace(KeyPrefix)) throw new InvalidOperationException("Cache:KeyPrefix é obrigatório.");
    }
}

public static class CacheMetrics
{
    public const string MeterName = "LibraryLoans.Cache";

    private static readonly Meter Meter = new(MeterName);
    public static readonly Counter<long> Hits = Meter.CreateCounter<long>("cache.hits");
    public static readonly Counter<long> Misses = Meter.CreateCounter<long>("cache.misses");
    public static readonly Counter<long> Errors = Meter.CreateCounter<long>("cache.errors");
    public static readonly Counter<long> Bypasses = Meter.CreateCounter<long>("cache.bypasses");
    public static readonly Counter<long> Invalidations = Meter.CreateCounter<long>("cache.invalidations");
}

// Chaves versionadas: "{prefix}:data:{area}:{versão da área}:{chave}". Invalidar = trocar a versão da área
// (uma escrita), sem varrer chaves. Entradas antigas ficam órfãs e expiram pelo TTL.
public sealed class RedisReadCache(
    IDistributedCache cache, CacheSettings settings, ILogger<RedisReadCache> logger) : IReadCache
{
    private static readonly TimeSpan VersionTtl = TimeSpan.FromHours(CacheSettings.VersionTtlHours);
    private long _openUntilTicks;

    public async Task<T> GetOrCreateAsync<T>(
        string area, string key, Func<CancellationToken, Task<T>> factory, CancellationToken cancellationToken)
    {
        var group = Group(area);
        if (IsOpen())
        {
            CacheMetrics.Bypasses.Add(1, Tag(group));
            return await factory(cancellationToken);
        }

        string? version = null;
        try
        {
            // A versão é lida antes da consulta ao banco: se uma invalidação ocorrer no meio, a escrita
            // abaixo cai numa versão já descartada e nunca é lida.
            version = await GetVersionAsync(area, cancellationToken);
            var cached = await cache.GetStringAsync(DataKey(area, version, key), cancellationToken);
            if (cached is not null)
            {
                var value = JsonSerializer.Deserialize<T>(cached, JsonSerializerOptions.Web);
                if (value is not null)
                {
                    CacheMetrics.Hits.Add(1, Tag(group));
                    return value;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Fail(ex, "leitura", group);
            version = null;
        }

        CacheMetrics.Misses.Add(1, Tag(group));
        var result = await factory(cancellationToken);

        if (version is not null)
        {
            try
            {
                await cache.SetStringAsync(
                    DataKey(area, version, key),
                    JsonSerializer.Serialize(result, JsonSerializerOptions.Web),
                    new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(settings.TtlSeconds) },
                    CancellationToken.None);
            }
            catch (Exception ex)
            {
                Fail(ex, "escrita", group);
            }
        }

        return result;
    }

    public async Task InvalidateAsync(params string[] areas)
    {
        if (IsOpen())
        {
            // Entradas que não puderem ser invalidadas expiram pelo TTL (consistência eventual limitada).
            foreach (var area in areas) CacheMetrics.Bypasses.Add(1, Tag(Group(area)));
            logger.LogWarning("Cache indisponível: invalidação de {Areas} ignorada; entradas expiram em até {Ttl}s.",
                string.Join(",", areas), settings.TtlSeconds);
            return;
        }

        foreach (var area in areas)
        {
            try
            {
                await cache.SetStringAsync(
                    VersionKey(area), Guid.NewGuid().ToString("N"),
                    new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = VersionTtl },
                    CancellationToken.None);
                CacheMetrics.Invalidations.Add(1, Tag(Group(area)));
            }
            catch (Exception ex)
            {
                Fail(ex, "invalidação", Group(area));
                return;
            }
        }
    }

    private async Task<string> GetVersionAsync(string area, CancellationToken ct) =>
        await cache.GetStringAsync(VersionKey(area), ct) ?? "0";

    private string VersionKey(string area) => $"{settings.KeyPrefix}:ver:{area}";
    private string DataKey(string area, string version, string key) => $"{settings.KeyPrefix}:data:{area}:{version}:{key}";

    private bool IsOpen() => Environment.TickCount64 < Interlocked.Read(ref _openUntilTicks);

    private void Fail(Exception ex, string operation, string group)
    {
        CacheMetrics.Errors.Add(1, Tag(group));
        Interlocked.Exchange(ref _openUntilTicks, Environment.TickCount64 + settings.CircuitBreakerSeconds * 1000L);
        logger.LogWarning(ex, "Falha no cache Redis ({Operation}, {Group}); usando PostgreSQL por {Seconds}s.",
            operation, group, settings.CircuitBreakerSeconds);
    }

    private static string Group(string area) => area.Split(':')[0];
    private static KeyValuePair<string, object?> Tag(string group) => new("cache.area", group);
}
