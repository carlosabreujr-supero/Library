using LibraryLoans.Api.Endpoints;
using LibraryLoans.Api.Infrastructure;
using LibraryLoans.Application;
using LibraryLoans.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

builder.Services
    .AddApplication()
    .AddInfrastructure(builder.Configuration);

var loanSettings = builder.Configuration.GetSection("Loans").Get<LibraryLoans.Application.Loans.LoanSettings>() ?? new();
loanSettings.Validate();
builder.Services.AddSingleton(loanSettings);

builder.Services.AddExceptionHandler<GlobalExceptionHandler>();
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<LibraryLoans.Application.Abstractions.IRequestContext, HttpRequestContext>();
builder.Services.AddProblemDetails(options =>
    options.CustomizeProblemDetails = ctx =>
    {
        ctx.ProblemDetails.Extensions["traceId"] = ctx.HttpContext.TraceIdentifier;
        if (ctx.HttpContext.Items[CorrelationIdMiddleware.ItemKey] is string correlationId)
            ctx.ProblemDetails.Extensions["correlationId"] = correlationId;
    });

var app = builder.Build();

// Correlation id primeiro, para que o tratamento de exceções e os logs já o incluam.
app.UseMiddleware<CorrelationIdMiddleware>();
app.UseExceptionHandler();
app.UseStatusCodePages();

app.MapBookEndpoints();
app.MapReaderEndpoints();
app.MapLoanEndpoints();
app.MapAuditEndpoints();

app.MapGet("/health", () => Results.Ok(new { status = "healthy" }));

app.MapGet("/health/ready", async (IConfiguration config, CancellationToken ct) =>
{
    await using var pg = new Npgsql.NpgsqlConnection(config.GetConnectionString("Postgres"));
    await pg.OpenAsync(ct);

    await using var redis = await StackExchange.Redis.ConnectionMultiplexer.ConnectAsync(config.GetConnectionString("Redis")!);
    await redis.GetDatabase().PingAsync();

    return Results.Ok(new { status = "ready", postgres = "up", redis = "up" });
});

app.Run();

public partial class Program;
