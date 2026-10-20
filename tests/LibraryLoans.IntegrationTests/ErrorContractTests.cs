using System.Net;
using System.Text.Json;
using LibraryLoans.Application.Exceptions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace LibraryLoans.IntegrationTests;

public class ErrorContractTests : IClassFixture<ErrorContractTests.Factory>
{
    public class Factory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("ConnectionStrings:Postgres", "Host=localhost;Database=test;Username=test;Password=test");
            builder.UseSetting("ConnectionStrings:Redis", "localhost:6379");
            builder.ConfigureServices(services =>
                services.AddTransient<IStartupFilter, ThrowingEndpointsFilter>());
        }
    }

    private sealed class ThrowingEndpointsFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            next(app);
            app.UseEndpoints(e =>
            {
                e.MapGet("/t/notfound", () => { throw new NotFoundException("Livro não encontrado."); });
                e.MapGet("/t/conflict", () => { throw new ConflictException("Sem exemplares disponíveis."); });
                e.MapGet("/t/validation", () => { throw new ValidationException("title", "Título é obrigatório."); });
                e.MapGet("/t/boom", () => { throw new InvalidOperationException("Host=db;Password=SEGREDO"); });
            });
        };
    }

    private readonly HttpClient _client;

    public ErrorContractTests(Factory factory) => _client = factory.CreateClient();

    [Theory]
    [InlineData("/t/notfound", HttpStatusCode.NotFound)]
    [InlineData("/t/conflict", HttpStatusCode.Conflict)]
    [InlineData("/t/validation", HttpStatusCode.BadRequest)]
    [InlineData("/t/boom", HttpStatusCode.InternalServerError)]
    [InlineData("/rota-inexistente", HttpStatusCode.NotFound)]
    public async Task Errors_use_problem_details_with_correlation_id(string path, HttpStatusCode expected)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add("X-Correlation-Id", "abc-123");

        var response = await _client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(expected, response.StatusCode);
        Assert.StartsWith("application/problem+json", response.Content.Headers.ContentType?.MediaType + "");
        Assert.Equal("abc-123", response.Headers.GetValues("X-Correlation-Id").Single());

        using var doc = JsonDocument.Parse(body);
        Assert.Equal((int)expected, doc.RootElement.GetProperty("status").GetInt32());
        Assert.Equal("abc-123", doc.RootElement.GetProperty("correlationId").GetString());
        Assert.DoesNotContain("SEGREDO", body);
        Assert.DoesNotContain("   at ", body);
    }

    [Fact]
    public async Task Validation_errors_are_listed_by_field()
    {
        var response = await _client.GetAsync("/t/validation");
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("Título é obrigatório.", doc.RootElement.GetProperty("errors").GetProperty("title")[0].GetString());
    }

    [Fact]
    public async Task Invalid_correlation_id_is_replaced()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/health");
        request.Headers.Add("X-Correlation-Id", "bad value\twith spaces");
        var response = await _client.SendAsync(request);
        Assert.NotEqual("bad value\twith spaces", response.Headers.GetValues("X-Correlation-Id").Single());
    }
}
