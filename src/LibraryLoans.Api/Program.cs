using LibraryLoans.Application;
using LibraryLoans.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

builder.Services
    .AddApplication()
    .AddInfrastructure(builder.Configuration);

var app = builder.Build();

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
