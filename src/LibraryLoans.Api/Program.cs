using LibraryLoans.Api.Endpoints;
using LibraryLoans.Api.Infrastructure;
using LibraryLoans.Application;
using LibraryLoans.Infrastructure;
using LibraryLoans.Infrastructure.Health;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;

var builder = WebApplication.CreateBuilder(args);

builder.Services
    .AddApplication()
    .AddInfrastructure(builder.Configuration);

var loanSettings = builder.Configuration.GetSection("Loans").Get<LibraryLoans.Application.Loans.LoanSettings>() ?? new();
loanSettings.Validate();
builder.Services.AddSingleton(loanSettings);

builder.Services.AddExceptionHandler<GlobalExceptionHandler>();
builder.Services.AddHealthChecks()
    .AddCheck<PostgresHealthCheck>("postgres", tags: ["ready"])
    .AddCheck<RedisHealthCheck>("redis", tags: ["ready"]);
builder.AddObservability();
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

// Liveness: só indica que o processo responde; não consulta nenhuma dependência.
app.MapHealthChecks("/health/live", new HealthCheckOptions
{
    Predicate = _ => false,
    ResponseWriter = HealthResponseWriter.WriteAsync
});

// Readiness: PostgreSQL é obrigatório (503 se cair); Redis é opcional por padrão (Degraded = 200).
app.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    Predicate = check => check.Tags.Contains("ready"),
    ResponseWriter = HealthResponseWriter.WriteAsync
});

app.Run();

public partial class Program;
