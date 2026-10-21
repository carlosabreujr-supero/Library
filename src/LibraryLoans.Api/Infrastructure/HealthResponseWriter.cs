using System.Text.Json;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace LibraryLoans.Api.Infrastructure;

public static class HealthResponseWriter
{
    // Não inclui exceções nem connection strings: apenas estado e descrição curta.
    public static Task WriteAsync(HttpContext context, HealthReport report)
    {
        context.Response.ContentType = "application/json";
        var body = new
        {
            status = report.Status.ToString(),
            checks = report.Entries.ToDictionary(
                e => e.Key,
                e => new { status = e.Value.Status.ToString(), description = e.Value.Description })
        };
        return context.Response.WriteAsync(JsonSerializer.Serialize(body, JsonSerializerOptions.Web));
    }
}
