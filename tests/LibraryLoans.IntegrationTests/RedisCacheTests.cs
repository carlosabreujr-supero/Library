using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using LibraryLoans.Infrastructure.Caching;
using LibraryLoans.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;

namespace LibraryLoans.IntegrationTests;

// Redis real (TEST_REDIS, ex.: localhost:6380). Prefixo exclusivo por fábrica: execuções e testes não se misturam.
public class RedisCacheFactory : PostgresFactory
{
    public string Prefix { get; } = $"test:{Guid.NewGuid():N}";
    public const int TtlSeconds = 30;

    public static string RedisConnection =>
        Environment.GetEnvironmentVariable("TEST_REDIS") is { Length: > 0 } c
            ? c
            : throw new InvalidOperationException("Defina TEST_REDIS (ex.: localhost:6380) para os testes de cache (ver docs/testing.md).");

    protected override void ConfigureCache(IWebHostBuilder builder)
    {
        builder.UseSetting("Cache:Enabled", "true");
        builder.UseSetting("Cache:KeyPrefix", Prefix);
        builder.UseSetting("Cache:TtlSeconds", TtlSeconds.ToString());
        builder.UseSetting("ConnectionStrings:Redis", RedisConnection);
    }
}

// Redis inacessível: simula indisponibilidade com o cache habilitado.
public sealed class UnreachableRedisFactory : PostgresFactory
{
    protected override void ConfigureCache(IWebHostBuilder builder)
    {
        builder.UseSetting("Cache:Enabled", "true");
        builder.UseSetting("Cache:TimeoutMs", "200");
        builder.UseSetting("Cache:CircuitBreakerSeconds", "30");
        builder.UseSetting("ConnectionStrings:Redis", "127.0.0.1:1");
    }
}

public sealed class CacheMetricsListener : IDisposable
{
    private readonly MeterListener _listener = new();
    private readonly Dictionary<string, long> _totals = [];

    public CacheMetricsListener()
    {
        _listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == CacheMetrics.MeterName) l.EnableMeasurementEvents(instrument);
        };
        _listener.SetMeasurementEventCallback<long>((instrument, value, _, _) =>
        {
            lock (_totals) _totals[instrument.Name] = _totals.GetValueOrDefault(instrument.Name) + value;
        });
        _listener.Start();
    }

    public long Total(string name)
    {
        lock (_totals) return _totals.GetValueOrDefault(name);
    }

    public void Dispose() => _listener.Dispose();
}

public abstract class CacheTestBase<TFactory>(TFactory factory) where TFactory : PostgresFactory
{
    protected TFactory Factory { get; } = factory;
    protected HttpClient Client { get; } = factory.CreateClient();

    protected static string NewIsbn()
    {
        var digits = "978" + Random.Shared.NextInt64(0, 1_000_000_000).ToString("D9");
        var sum = digits.Select((c, i) => (c - '0') * (i % 2 == 0 ? 1 : 3)).Sum();
        return digits + (10 - sum % 10) % 10;
    }

    protected static async Task<JsonElement> Json(HttpResponseMessage r) =>
        JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement.Clone();

    protected async Task<Guid> CreateBook(string title, int total)
    {
        var r = await Client.PostAsJsonAsync("/books", new { title, author = "A", isbn = NewIsbn(), totalCopies = total });
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        return (await Json(r)).GetProperty("id").GetGuid();
    }

    protected async Task<Guid> CreateReader()
    {
        var r = await Client.PostAsJsonAsync("/users", new { name = "Leitor" });
        return (await Json(r)).GetProperty("id").GetGuid();
    }

    protected async Task<JsonElement> List(string search)
    {
        var r = await Client.GetAsync($"/books?search={Uri.EscapeDataString(search)}");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return await Json(r);
    }

    protected async Task<JsonElement> Availability(Guid bookId)
    {
        var r = await Client.GetAsync($"/books/{bookId}/availability");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return await Json(r);
    }

    protected async Task Sql(FormattableString sql)
    {
        using var scope = Factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<LibraryDbContext>().Database.ExecuteSqlInterpolatedAsync(sql);
    }

    protected async Task<HttpResponseMessage> Borrow(Guid bookId, Guid readerId)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/loans") { Content = JsonContent.Create(new { bookId, readerId }) };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        return await Client.SendAsync(request);
    }

    protected static string Titles(JsonElement page) =>
        string.Join(",", page.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("title").GetString()));
}

public class RedisCacheTests(RedisCacheFactory factory) : CacheTestBase<RedisCacheFactory>(factory), IClassFixture<RedisCacheFactory>
{
    [PostgresFact]
    public async Task Repeated_list_is_served_from_cache()
    {
        using var metrics = new CacheMetricsListener();
        var token = $"hit-{Guid.NewGuid():N}";
        var id = await CreateBook(token, 1);

        Assert.Equal(token, Titles(await List(token)));

        // Alteração direta no banco (fora da API): só um hit explica o valor antigo.
        await Sql($"UPDATE books SET \"Title\" = {token + "-db"} WHERE \"Id\" = {id}");

        Assert.Equal(token, Titles(await List(token)));
        Assert.True(metrics.Total("cache.hits") >= 1);
        Assert.True(metrics.Total("cache.misses") >= 1);
    }

    [PostgresFact]
    public async Task Book_create_update_and_deactivate_invalidate_the_list()
    {
        var token = $"inv-{Guid.NewGuid():N}";

        Assert.Equal("", Titles(await List(token))); // resultado vazio fica em cache

        var id = await CreateBook(token, 1);
        Assert.Equal(token, Titles(await List(token)));

        var patch = await Client.PatchAsJsonAsync($"/books/{id}", new { title = token + "-novo" });
        Assert.Equal(HttpStatusCode.OK, patch.StatusCode);
        Assert.Equal(token + "-novo", Titles(await List(token)));

        Assert.Equal(HttpStatusCode.NoContent, (await Client.DeleteAsync($"/books/{id}")).StatusCode);
        Assert.Equal("", Titles(await List(token)));
    }

    [PostgresFact]
    public async Task Loans_returns_cancellations_and_quantity_changes_invalidate_availability()
    {
        var bookId = await CreateBook($"disp-{Guid.NewGuid():N}", 2);
        var readerId = await CreateReader();

        Assert.Equal(2, (await Availability(bookId)).GetProperty("availableCopies").GetInt32());

        var loan1 = await Json(await Borrow(bookId, readerId));
        Assert.Equal(1, (await Availability(bookId)).GetProperty("availableCopies").GetInt32());

        Assert.Equal(HttpStatusCode.OK, (await Client.PostAsync($"/loans/{loan1.GetProperty("id").GetGuid()}/return", null)).StatusCode);
        Assert.Equal(2, (await Availability(bookId)).GetProperty("availableCopies").GetInt32());

        var loan2 = await Json(await Borrow(bookId, readerId));
        Assert.Equal(1, (await Availability(bookId)).GetProperty("availableCopies").GetInt32());

        Assert.Equal(HttpStatusCode.OK, (await Client.PostAsync($"/loans/{loan2.GetProperty("id").GetGuid()}/cancel", null)).StatusCode);
        Assert.Equal(2, (await Availability(bookId)).GetProperty("availableCopies").GetInt32());

        await Client.PatchAsJsonAsync($"/books/{bookId}", new { totalCopies = 4 });
        var afterPatch = await Availability(bookId);
        Assert.Equal(4, afterPatch.GetProperty("availableCopies").GetInt32());
        Assert.Equal(4, afterPatch.GetProperty("totalCopies").GetInt32());

        await Client.DeleteAsync($"/books/{bookId}");
        Assert.False((await Availability(bookId)).GetProperty("isAvailable").GetBoolean());
    }

    [PostgresFact]
    public async Task Loan_decision_never_trusts_the_cache()
    {
        var bookId = await CreateBook($"auth-{Guid.NewGuid():N}", 1);
        var readerId = await CreateReader();

        // Cache diz "disponível", o PostgreSQL diz "esgotado": o empréstimo deve ser recusado.
        Assert.Equal(1, (await Availability(bookId)).GetProperty("availableCopies").GetInt32());
        await Sql($"UPDATE books SET \"AvailableCopies\" = 0 WHERE \"Id\" = {bookId}");
        Assert.Equal(1, (await Availability(bookId)).GetProperty("availableCopies").GetInt32()); // ainda em cache
        Assert.Equal(HttpStatusCode.Conflict, (await Borrow(bookId, readerId)).StatusCode);

        // Inverso: cache diz "esgotado", o PostgreSQL diz "disponível": o empréstimo deve ser aceito.
        await Sql($"UPDATE books SET \"AvailableCopies\" = 1 WHERE \"Id\" = {bookId}");
        Assert.Equal(HttpStatusCode.Created, (await Borrow(bookId, readerId)).StatusCode);
    }

    [PostgresFact]
    public async Task Concurrent_loans_with_cache_enabled_still_reserve_only_available_copies()
    {
        var bookId = await CreateBook($"conc-{Guid.NewGuid():N}", 1);
        var readerId = await CreateReader();
        await Availability(bookId);

        var responses = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => Borrow(bookId, readerId)));

        Assert.Equal(1, responses.Count(r => r.StatusCode == HttpStatusCode.Created));
        Assert.Equal(19, responses.Count(r => r.StatusCode == HttpStatusCode.Conflict));
        Assert.Equal(0, (await Availability(bookId)).GetProperty("availableCopies").GetInt32());
    }

    [PostgresFact]
    public async Task Keys_are_versioned_prefixed_and_expire()
    {
        var token = $"key-{Guid.NewGuid():N}";
        await CreateBook(token, 1);
        await List(token);

        await using var redis = await ConnectionMultiplexer.ConnectAsync(RedisCacheFactory.RedisConnection);
        var db = redis.GetDatabase();
        var server = redis.GetServers().First();
        var keys = server.Keys(pattern: $"{Factory.Prefix}:data:books:list:*").ToArray();

        Assert.NotEmpty(keys);
        foreach (var key in keys)
        {
            var ttl = await db.KeyTimeToLiveAsync(key);
            Assert.NotNull(ttl);
            Assert.InRange(ttl!.Value.TotalSeconds, 1, RedisCacheFactory.TtlSeconds);
        }

        var versionBefore = (string?)await db.HashGetAsync($"{Factory.Prefix}:ver:books:list", "data");
        await CreateBook(token + "x", 1); // invalida a lista
        var versionAfter = (string?)await db.HashGetAsync($"{Factory.Prefix}:ver:books:list", "data");

        Assert.NotNull(versionAfter);
        Assert.NotEqual(versionBefore, versionAfter);
    }
}

// Uma fábrica por teste: o circuit breaker é estado do processo e não deve vazar entre testes.
public sealed class RedisFallbackTests : CacheTestBase<UnreachableRedisFactory>, IDisposable
{
    public RedisFallbackTests() : base(NewFactory()) { }

    private static UnreachableRedisFactory NewFactory()
    {
        var factory = new UnreachableRedisFactory();
        ((IAsyncLifetime)factory).InitializeAsync().GetAwaiter().GetResult();
        return factory;
    }

    public void Dispose() => Factory.Dispose();

    [PostgresFact]
    public async Task Reads_and_writes_work_with_redis_down_and_failures_are_counted()
    {
        using var metrics = new CacheMetricsListener();
        var token = $"down-{Guid.NewGuid():N}";

        var id = await CreateBook(token, 2); // invalidação falha, mas o cadastro é confirmado
        Assert.Equal(token, Titles(await List(token)));
        Assert.Equal(2, (await Availability(id)).GetProperty("availableCopies").GetInt32());

        var readerId = await CreateReader();
        Assert.Equal(HttpStatusCode.Created, (await Borrow(id, readerId)).StatusCode);
        Assert.Equal(1, (await Availability(id)).GetProperty("availableCopies").GetInt32()); // sempre do PostgreSQL

        Assert.True(metrics.Total("cache.errors") >= 1);
        Assert.True(metrics.Total("cache.bypasses") >= 1);
    }

    [PostgresFact]
    public async Task Loan_integrity_is_preserved_with_redis_down()
    {
        var bookId = await CreateBook($"down-conc-{Guid.NewGuid():N}", 1);
        var readerId = await CreateReader();

        var responses = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => Borrow(bookId, readerId)));

        Assert.Equal(1, responses.Count(r => r.StatusCode == HttpStatusCode.Created));
        Assert.Equal(19, responses.Count(r => r.StatusCode == HttpStatusCode.Conflict));
        Assert.Equal(0, (await Availability(bookId)).GetProperty("availableCopies").GetInt32());
    }
}
