using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using LibraryLoans.Application;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace LibraryLoans.IntegrationTests;

public sealed record CapturedLog(
    string Category, LogLevel Level, string Message,
    IReadOnlyDictionary<string, object?> State, IReadOnlyDictionary<string, object?> Scope);

public sealed class CapturingLoggerProvider : ILoggerProvider, ISupportExternalScope
{
    private IExternalScopeProvider _scopes = new LoggerExternalScopeProvider();
    public ConcurrentQueue<CapturedLog> Entries { get; } = new();

    public void SetScopeProvider(IExternalScopeProvider scopeProvider) => _scopes = scopeProvider;
    public ILogger CreateLogger(string categoryName) => new CapturingLogger(this, categoryName);
    public void Dispose() { }

    private sealed class CapturingLogger(CapturingLoggerProvider owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => owner._scopes.Push(state);
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var stateValues = new Dictionary<string, object?>();
            if (state is IEnumerable<KeyValuePair<string, object?>> kvps)
                foreach (var kv in kvps) stateValues[kv.Key] = kv.Value;

            var scope = new Dictionary<string, object?>();
            owner._scopes.ForEachScope((s, d) =>
            {
                if (s is IEnumerable<KeyValuePair<string, object?>> items)
                    foreach (var kv in items) d[kv.Key] = kv.Value;
            }, scope);

            owner.Entries.Enqueue(new CapturedLog(category, logLevel, formatter(state, exception), stateValues, scope));
        }
    }
}

public class ObservabilityFactory : PostgresFactory
{
    public CapturingLoggerProvider Logs { get; } = new();
    public Dictionary<string, string?> Settings { get; } = [];

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        foreach (var (key, value) in Settings) builder.UseSetting(key, value);
        builder.ConfigureServices(s => s.AddSingleton<ILoggerProvider>(Logs));
    }
}

public sealed class UnreachableRedisHealthFactory(bool redisRequired) : ObservabilityFactory
{
    protected override void ConfigureCache(IWebHostBuilder builder)
    {
        builder.UseSetting("Cache:Enabled", "true");
        builder.UseSetting("Cache:TimeoutMs", "200");
        builder.UseSetting("Health:TimeoutMs", "1000");
        builder.UseSetting("Health:RedisRequired", redisRequired ? "true" : "false");
        builder.UseSetting("ConnectionStrings:Redis", "127.0.0.1:1");
    }
}

// Sem PostgreSQL alcançável: a API deve continuar "viva", mas não "pronta".
public sealed class UnreachablePostgresFactory : Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("ConnectionStrings:Postgres", "Host=127.0.0.1;Port=1;Database=x;Username=u-secret-user;Password=p-secret-pass;Timeout=1");
        builder.UseSetting("ConnectionStrings:Redis", "localhost:6379");
        builder.UseSetting("Cache:Enabled", "false");
        builder.UseSetting("Health:TimeoutMs", "1500");
    }
}

public sealed class MetricsCapture : IDisposable
{
    public sealed record Measurement(string Instrument, double Value, IReadOnlyDictionary<string, object?> Tags);

    private readonly MeterListener _listener = new();
    public ConcurrentQueue<Measurement> Measurements { get; } = new();

    public MetricsCapture(string meterName)
    {
        _listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == meterName) l.EnableMeasurementEvents(instrument);
        };
        _listener.SetMeasurementEventCallback<long>((i, v, tags, _) => Add(i, v, tags));
        _listener.SetMeasurementEventCallback<double>((i, v, tags, _) => Add(i, v, tags));
        _listener.Start();
    }

    private void Add(Instrument instrument, double value, ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        var dict = new Dictionary<string, object?>();
        foreach (var t in tags) dict[t.Key] = t.Value;
        Measurements.Enqueue(new Measurement(instrument.Name, value, dict));
    }

    public double Sum(string instrument, Func<Measurement, bool>? filter = null) =>
        Measurements.Where(m => m.Instrument == instrument && (filter?.Invoke(m) ?? true)).Sum(m => m.Value);

    public int Count(string instrument, Func<Measurement, bool>? filter = null) =>
        Measurements.Count(m => m.Instrument == instrument && (filter?.Invoke(m) ?? true));

    public void Dispose() => _listener.Dispose();
}

// Estes testes observam instrumentos globais do processo: rodam isolados dos demais.
[CollectionDefinition("Observability", DisableParallelization = true)]
public sealed class ObservabilityCollection : ICollectionFixture<ObservabilityFactory>;

public class HealthCheckTests
{
    private static async Task<(HttpStatusCode Status, JsonElement Body, string Raw)> Get(HttpClient client, string path)
    {
        var response = await client.GetAsync(path);
        var raw = await response.Content.ReadAsStringAsync();
        return (response.StatusCode, JsonDocument.Parse(raw).RootElement.Clone(), raw);
    }

    [Fact]
    public async Task Live_is_healthy_even_when_dependencies_are_down_and_ready_is_503_without_leaking_secrets()
    {
        using var factory = new UnreachablePostgresFactory();
        var client = factory.CreateClient();

        var live = await Get(client, "/health/live");
        Assert.Equal(HttpStatusCode.OK, live.Status);
        Assert.Equal("Healthy", live.Body.GetProperty("status").GetString());
        Assert.Empty(live.Body.GetProperty("checks").EnumerateObject());

        var ready = await Get(client, "/health/ready");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, ready.Status);
        Assert.Equal("Unhealthy", ready.Body.GetProperty("checks").GetProperty("postgres").GetProperty("status").GetString());
        Assert.DoesNotContain("secret", ready.Raw);
        Assert.DoesNotContain("127.0.0.1", ready.Raw);
    }

    [PostgresFact]
    public async Task Ready_is_degraded_but_200_when_only_optional_redis_is_down()
    {
        using var factory = new UnreachableRedisHealthFactory(redisRequired: false);
        await ((IAsyncLifetime)factory).InitializeAsync();
        var client = factory.CreateClient();

        var ready = await Get(client, "/health/ready");

        Assert.Equal(HttpStatusCode.OK, ready.Status);
        Assert.Equal("Degraded", ready.Body.GetProperty("status").GetString());
        Assert.Equal("Healthy", ready.Body.GetProperty("checks").GetProperty("postgres").GetProperty("status").GetString());
        Assert.Equal("Degraded", ready.Body.GetProperty("checks").GetProperty("redis").GetProperty("status").GetString());
        Assert.Equal(HttpStatusCode.OK, (await Get(client, "/health/live")).Status);
    }

    [PostgresFact]
    public async Task Ready_is_503_when_redis_is_configured_as_required_and_down()
    {
        using var factory = new UnreachableRedisHealthFactory(redisRequired: true);
        await ((IAsyncLifetime)factory).InitializeAsync();
        var client = factory.CreateClient();

        var ready = await Get(client, "/health/ready");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, ready.Status);
        Assert.Equal("Unhealthy", ready.Body.GetProperty("checks").GetProperty("redis").GetProperty("status").GetString());
        Assert.Equal(HttpStatusCode.OK, (await Get(client, "/health/live")).Status);
    }

    [PostgresFact]
    public async Task Ready_is_healthy_when_postgres_and_redis_are_up()
    {
        using var factory = new RedisCacheFactory();
        await ((IAsyncLifetime)factory).InitializeAsync();
        var client = factory.CreateClient();

        var ready = await Get(client, "/health/ready");

        Assert.Equal(HttpStatusCode.OK, ready.Status);
        Assert.Equal("Healthy", ready.Body.GetProperty("status").GetString());
        Assert.Equal("Healthy", ready.Body.GetProperty("checks").GetProperty("redis").GetProperty("status").GetString());
    }
}

[Collection("Observability")]
public class ObservabilityTests(ObservabilityFactory factory)
{
    private readonly HttpClient _client = factory.CreateClient();

    private static string NewIsbn()
    {
        var digits = "978" + Random.Shared.NextInt64(0, 1_000_000_000).ToString("D9");
        var sum = digits.Select((c, i) => (c - '0') * (i % 2 == 0 ? 1 : 3)).Sum();
        return digits + (10 - sum % 10) % 10;
    }

    private static async Task<JsonElement> Json(HttpResponseMessage r) =>
        JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement.Clone();

    private async Task<(Guid BookId, Guid ReaderId)> Setup(int copies)
    {
        var book = await Json(await _client.PostAsJsonAsync("/books", new { title = "T", author = "A", isbn = NewIsbn(), totalCopies = copies }));
        var reader = await Json(await _client.PostAsJsonAsync("/users", new { name = "R" }));
        return (book.GetProperty("id").GetGuid(), reader.GetProperty("id").GetGuid());
    }

    private Task<HttpResponseMessage> Borrow(Guid bookId, Guid readerId, string? key = null, string? traceparent = null, string? correlationId = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/loans") { Content = JsonContent.Create(new { bookId, readerId }) };
        request.Headers.Add("Idempotency-Key", key ?? Guid.NewGuid().ToString());
        if (traceparent is not null) request.Headers.Add("traceparent", traceparent);
        if (correlationId is not null) request.Headers.Add("X-Correlation-Id", correlationId);
        return _client.SendAsync(request);
    }

    [PostgresFact]
    public async Task Loan_metrics_count_created_rejected_replayed_and_record_latency_without_business_ids()
    {
        var (bookId, readerId) = await Setup(1);
        var otherReader = (await Json(await _client.PostAsJsonAsync("/users", new { name = "R2" }))).GetProperty("id").GetGuid();
        using var metrics = new MetricsCapture(LoanTelemetry.Name);

        var key = Guid.NewGuid().ToString();
        Assert.Equal(HttpStatusCode.Created, (await Borrow(bookId, readerId, key)).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await Borrow(bookId, readerId, key)).StatusCode); // replay
        Assert.Equal(HttpStatusCode.Conflict, (await Borrow(bookId, otherReader)).StatusCode);   // sem exemplares
        Assert.Equal(HttpStatusCode.Conflict, (await Borrow(bookId, otherReader, key)).StatusCode); // chave reutilizada

        Assert.Equal(1, metrics.Sum("library.loans.created"));
        Assert.Equal(1, metrics.Sum("library.loans.idempotent_replays"));
        Assert.Equal(1, metrics.Sum("library.loans.rejected", m => (string?)m.Tags["reason"] == "unavailable"));
        Assert.Equal(1, metrics.Sum("library.loans.rejected", m => (string?)m.Tags["reason"] == "idempotency_key_reuse"));
        Assert.Equal(4, metrics.Count("library.loans.create.duration"));
        Assert.All(metrics.Measurements.Where(m => m.Instrument == "library.loans.create.duration"), m => Assert.True(m.Value >= 0));

        // Cardinalidade: somente tags de domínio fechado; nenhum ID de negócio.
        var allowedTags = new HashSet<string> { "reason", "outcome", "status" };
        Assert.All(metrics.Measurements, m =>
        {
            Assert.Subset(allowedTags, m.Tags.Keys.ToHashSet());
            Assert.DoesNotContain(m.Tags.Values, v => v is Guid || Guid.TryParse(v?.ToString(), out _));
        });
    }

    [PostgresFact]
    public async Task Loan_flow_is_traced_with_context_propagation_and_database_spans()
    {
        var (bookId, readerId) = await Setup(1);
        var traceId = ActivityTraceId.CreateRandom();
        var traceparent = $"00-{traceId}-{ActivitySpanId.CreateRandom()}-01";
        var activities = new ConcurrentQueue<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name is LoanTelemetry.Name or "Npgsql" or "Microsoft.AspNetCore",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = a => { if (a.TraceId == traceId) activities.Enqueue(a); }
        };
        ActivitySource.AddActivityListener(listener);

        var response = await Borrow(bookId, readerId, traceparent: traceparent);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var all = activities.ToArray();
        var server = Assert.Single(all, a => a.Source.Name == "Microsoft.AspNetCore");
        var createLoan = Assert.Single(all, a => a.OperationName == "CreateLoan");
        Assert.Contains(all, a => a.OperationName == "ReserveCopy");
        Assert.Contains(all, a => a.Source.Name == "Npgsql");

        Assert.Equal(traceId, server.TraceId);
        Assert.Equal("created", createLoan.GetTagItem("library.outcome"));
        Assert.Equal(bookId.ToString(), createLoan.GetTagItem("library.book_id")?.ToString());
        Assert.NotNull(createLoan.GetTagItem("library.loan_id"));
    }

    [PostgresFact]
    public async Task Rejected_loan_span_carries_the_reason()
    {
        var (bookId, readerId) = await Setup(0);
        var traceId = ActivityTraceId.CreateRandom();
        var activities = new ConcurrentQueue<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == LoanTelemetry.Name,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = a => { if (a.TraceId == traceId) activities.Enqueue(a); }
        };
        ActivitySource.AddActivityListener(listener);

        var response = await Borrow(bookId, readerId, traceparent: $"00-{traceId}-{ActivitySpanId.CreateRandom()}-01");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var span = Assert.Single(activities, a => a.OperationName == "CreateLoan");
        Assert.Equal("unavailable", span.GetTagItem("library.rejection_reason"));
        Assert.Equal("rejected", span.GetTagItem("library.outcome"));
    }

    [PostgresFact]
    public async Task Logs_are_structured_with_correlation_and_business_ids_and_without_secrets()
    {
        var (bookId, readerId) = await Setup(1);
        var correlationId = $"corr-{Guid.NewGuid():N}";

        var response = await Borrow(bookId, readerId, correlationId: correlationId);
        var loanId = (await Json(response)).GetProperty("id").GetGuid();

        var entry = Assert.Single(factory.Logs.Entries, e =>
            e.Message.StartsWith("Empréstimo criado") && Equals(e.Scope.GetValueOrDefault("correlationId"), correlationId));
        Assert.Equal(loanId.ToString(), entry.State["LoanId"]?.ToString());
        Assert.Equal(bookId.ToString(), entry.State["BookId"]?.ToString());
        Assert.Equal(readerId.ToString(), entry.State["ReaderId"]?.ToString());

        var password = Environment.GetEnvironmentVariable("TEST_POSTGRES")!
            .Split(';').Select(p => p.Split('=', 2)).Where(p => p[0].Trim() is "Password" or "Pwd").Select(p => p[1]).FirstOrDefault();
        if (!string.IsNullOrEmpty(password))
            Assert.DoesNotContain(factory.Logs.Entries, e => e.Message.Contains(password));
    }

    [PostgresFact]
    public async Task Return_and_cancel_emit_metric_and_log()
    {
        var (bookId, readerId) = await Setup(2);
        var loan1 = (await Json(await Borrow(bookId, readerId))).GetProperty("id").GetGuid();
        var loan2 = (await Json(await Borrow(bookId, readerId))).GetProperty("id").GetGuid();
        using var metrics = new MetricsCapture(LoanTelemetry.Name);

        await _client.PostAsync($"/loans/{loan1}/return", null);
        await _client.PostAsync($"/loans/{loan2}/cancel", null);

        Assert.Equal(1, metrics.Sum("library.loans.closed", m => (string?)m.Tags["status"] == "Returned"));
        Assert.Equal(1, metrics.Sum("library.loans.closed", m => (string?)m.Tags["status"] == "Cancelled"));
        Assert.Contains(factory.Logs.Entries, e => e.State.GetValueOrDefault("LoanId")?.ToString() == loan1.ToString() && e.Message.Contains("Returned"));
    }
}

// Coletor OTLP/HTTP mínimo: prova que traces e métricas são exportados apenas por configuração externa.
public class OtlpExportTests
{
    private sealed class FakeCollector : IDisposable
    {
        private readonly HttpListener _listener = new();
        public int Port { get; }
        public TaskCompletionSource<bool> Traces { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> Metrics { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public FakeCollector()
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            Port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            _listener.Prefixes.Add($"http://localhost:{Port}/");
            _listener.Start();
            _ = Task.Run(Loop);
        }

        private async Task Loop()
        {
            while (_listener.IsListening)
            {
                HttpListenerContext ctx;
                try { ctx = await _listener.GetContextAsync(); } catch { return; }
                var path = ctx.Request.Url!.AbsolutePath;
                if (path == "/v1/traces") Traces.TrySetResult(true);
                if (path == "/v1/metrics") Metrics.TrySetResult(true);
                ctx.Response.StatusCode = 200;
                ctx.Response.Close();
            }
        }

        public void Dispose() => _listener.Close();
    }

    [PostgresFact]
    public async Task Traces_and_metrics_are_exported_via_otlp_when_configured_externally()
    {
        using var collector = new FakeCollector();
        using var factory = new ObservabilityFactory();
        factory.Settings["OTEL_EXPORTER_OTLP_ENDPOINT"] = $"http://localhost:{collector.Port}";
        factory.Settings["OTEL_EXPORTER_OTLP_PROTOCOL"] = "http/protobuf";
        factory.Settings["OTEL_BSP_SCHEDULE_DELAY"] = "200";
        factory.Settings["OTEL_METRIC_EXPORT_INTERVAL"] = "500";
        await ((IAsyncLifetime)factory).InitializeAsync();
        var client = factory.CreateClient();

        var book = JsonDocument.Parse(await (await client.PostAsJsonAsync("/books",
            new { title = "T", author = "A", isbn = "9780306406157", totalCopies = 1 })).Content.ReadAsStringAsync()).RootElement;
        // O ISBN fixo pode já existir de execuções anteriores (409); o importante é gerar tráfego instrumentado.
        _ = book;

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await collector.Traces.Task.WaitAsync(timeout.Token);
        await collector.Metrics.Task.WaitAsync(timeout.Token);
    }
}
