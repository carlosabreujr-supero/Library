using LibraryLoans.Application;
using LibraryLoans.Infrastructure.Caching;
using OpenTelemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace LibraryLoans.Api.Infrastructure;

public static class ObservabilityExtensions
{
    // Configuração 100% externa, pelas variáveis padrão do OpenTelemetry (sem vendor específico):
    // OTEL_EXPORTER_OTLP_ENDPOINT, OTEL_EXPORTER_OTLP_PROTOCOL, OTEL_EXPORTER_OTLP_HEADERS,
    // OTEL_SERVICE_NAME, OTEL_RESOURCE_ATTRIBUTES, OTEL_TRACES_SAMPLER.
    // Sem endpoint, os instrumentos continuam ativos (dotnet-counters etc.), mas nada é exportado.
    public static WebApplicationBuilder AddObservability(this WebApplicationBuilder builder)
    {
        var serviceName = builder.Configuration["OTEL_SERVICE_NAME"] ?? "LibraryLoans.Api";

        var otel = builder.Services.AddOpenTelemetry()
            .ConfigureResource(r => r.AddService(serviceName))
            .WithTracing(t => t
                .AddSource(LoanTelemetry.Name)
                .AddAspNetCoreInstrumentation(o =>
                    o.Filter = ctx => !ctx.Request.Path.StartsWithSegments("/health"))
                // Npgsql 8+ emite spans e métricas nativamente (ActivitySource/Meter "Npgsql").
                .AddSource("Npgsql"))
            .WithMetrics(m => m
                .AddMeter(LoanTelemetry.Name, CacheMetrics.MeterName, "Npgsql")
                .AddAspNetCoreInstrumentation())
            .WithLogging();

        if (!string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]))
            otel.UseOtlpExporter();

        builder.Logging.AddOpenTelemetry(o =>
        {
            o.IncludeScopes = true;
            o.IncludeFormattedMessage = true;
        });

        return builder;
    }
}
