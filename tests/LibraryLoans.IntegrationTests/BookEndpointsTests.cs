using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using LibraryLoans.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace LibraryLoans.IntegrationTests;

// Requer PostgreSQL: defina TEST_POSTGRES, ex.: Host=localhost;Port=5433;Database=library_loans_tests;Username=library;Password=<senha>
public sealed class PostgresFactAttribute : FactAttribute
{
    // Testes críticos nunca são ignorados: sem TEST_POSTGRES, a fábrica falha de forma explícita.
}

public sealed class PostgresTheoryAttribute : TheoryAttribute
{
    // Idem: nunca ignorado.
}

public class PostgresFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("ConnectionStrings:Postgres", RequireConnectionString());
        builder.UseSetting("ConnectionStrings:Redis", "localhost:6379");
        ConfigureCache(builder);
    }

    // Por padrão o cache fica desligado: os demais testes validam o comportamento direto do PostgreSQL.
    protected virtual void ConfigureCache(IWebHostBuilder builder) => builder.UseSetting("Cache:Enabled", "false");

    private static string RequireConnectionString() =>
        Environment.GetEnvironmentVariable("TEST_POSTGRES") is { Length: > 0 } cs
            ? cs
            : throw new InvalidOperationException(
                "Defina TEST_POSTGRES com a connection string de um PostgreSQL de testes (ver docs/testing.md).");

    public async ValueTask InitializeAsync() => await ((IAsyncLifetime)this).InitializeAsync();

    async Task IAsyncLifetime.InitializeAsync()
    {
        using var scope = Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<LibraryDbContext>().Database.MigrateAsync();
    }

    async Task IAsyncLifetime.DisposeAsync() => await base.DisposeAsync();
}

public class BookEndpointsTests(PostgresFactory factory) : IClassFixture<PostgresFactory>
{
    private readonly HttpClient _client = factory.CreateClient();

    private static string NewIsbn()
    {
        var digits = "978" + Random.Shared.NextInt64(0, 1_000_000_000).ToString("D9");
        var sum = digits.Select((c, i) => (c - '0') * (i % 2 == 0 ? 1 : 3)).Sum();
        return digits + (10 - sum % 10) % 10;
    }

    private async Task<(HttpResponseMessage Response, JsonElement Body)> Create(string? isbn = null, int total = 3, string author = "Autor")
    {
        var response = await _client.PostAsJsonAsync("/books", new { title = "Livro", author, isbn = isbn ?? NewIsbn(), totalCopies = total });
        var text = await response.Content.ReadAsStringAsync();
        return (response, JsonDocument.Parse(text).RootElement.Clone());
    }

    private async Task<int> AuditCount(Guid bookId, string action)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LibraryDbContext>();
        return await db.AuditEvents.CountAsync(a => a.EntityId == bookId && a.Action == action);
    }

    [PostgresFact]
    public async Task Create_returns_201_sets_available_equal_total_and_audits()
    {
        var (response, body) = await Create(total: 4);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var id = body.GetProperty("id").GetGuid();
        Assert.EndsWith($"/books/{id}", response.Headers.Location!.ToString());
        Assert.Equal(4, body.GetProperty("availableCopies").GetInt32());
        Assert.Equal(1, await AuditCount(id, "BookCreated"));
    }

    [PostgresFact]
    public async Task Duplicate_isbn_returns_409()
    {
        var isbn = NewIsbn();
        Assert.Equal(HttpStatusCode.Created, (await Create(isbn)).Response.StatusCode);

        var (response, body) = await Create(isbn);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("ISBN já cadastrado.", body.GetProperty("detail").GetString());
    }

    [PostgresFact]
    public async Task Invalid_payload_returns_400_with_field_errors()
    {
        var response = await _client.PostAsJsonAsync("/books", new { title = "", author = "", isbn = "123", totalCopies = -1 });
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var errors = body.GetProperty("errors");
        foreach (var field in new[] { "title", "author", "isbn", "totalCopies" })
            Assert.True(errors.TryGetProperty(field, out _), field);
    }

    [PostgresFact]
    public async Task Available_copies_cannot_be_set_through_crud()
    {
        var create = await _client.PostAsJsonAsync("/books", new { title = "L", author = "A", isbn = NewIsbn(), totalCopies = 1, availableCopies = 5 });
        Assert.Equal(HttpStatusCode.BadRequest, create.StatusCode);

        var (_, book) = await Create();
        var patch = await _client.PatchAsJsonAsync($"/books/{book.GetProperty("id").GetGuid()}", new { availableCopies = 0 });
        Assert.Equal(HttpStatusCode.BadRequest, patch.StatusCode);
    }

    [PostgresFact]
    public async Task Patch_cannot_reduce_total_below_borrowed_copies()
    {
        var (_, book) = await Create(total: 5);
        var id = book.GetProperty("id").GetGuid();

        // Simula 4 exemplares emprestados (empréstimos serão implementados em outra spec).
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LibraryDbContext>();
            await db.Books.Where(b => b.Id == id).ExecuteUpdateAsync(s => s.SetProperty(b => b.AvailableCopies, 1));
        }

        var rejected = await _client.PatchAsJsonAsync($"/books/{id}", new { totalCopies = 3 });
        Assert.Equal(HttpStatusCode.Conflict, rejected.StatusCode);

        var accepted = await _client.PatchAsJsonAsync($"/books/{id}", new { totalCopies = 4 });
        var body = JsonDocument.Parse(await accepted.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        Assert.Equal(0, body.GetProperty("availableCopies").GetInt32());
        Assert.Equal(1, await AuditCount(id, "BookUpdated"));
    }

    [PostgresFact]
    public async Task Patch_to_existing_isbn_returns_409()
    {
        var (_, first) = await Create();
        var (_, second) = await Create();

        var response = await _client.PatchAsJsonAsync($"/books/{second.GetProperty("id").GetGuid()}", new { isbn = first.GetProperty("isbn").GetString() });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [PostgresFact]
    public async Task Delete_deactivates_preserving_record_and_is_idempotent()
    {
        var (_, book) = await Create();
        var id = book.GetProperty("id").GetGuid();

        Assert.Equal(HttpStatusCode.NoContent, (await _client.DeleteAsync($"/books/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await _client.DeleteAsync($"/books/{id}")).StatusCode);

        var get = await _client.GetAsync($"/books/{id}");
        var body = JsonDocument.Parse(await get.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        Assert.False(body.GetProperty("isActive").GetBoolean());
        Assert.Equal(1, await AuditCount(id, "BookDeactivated"));

        Assert.Equal(HttpStatusCode.Conflict, (await _client.PatchAsJsonAsync($"/books/{id}", new { title = "Novo" })).StatusCode);
    }

    [PostgresFact]
    public async Task Unknown_book_returns_404()
    {
        var id = Guid.NewGuid();
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync($"/books/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.DeleteAsync($"/books/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.PatchAsJsonAsync($"/books/{id}", new { title = "x" })).StatusCode);
    }

    [PostgresFact]
    public async Task List_is_paginated_and_hides_inactive_by_default()
    {
        var author = "Autor-" + Guid.NewGuid().ToString("N");
        Guid inactive = default;
        for (var i = 0; i < 3; i++)
        {
            var (_, b) = await Create(author: author);
            if (i == 0) inactive = b.GetProperty("id").GetGuid();
        }
        await _client.DeleteAsync($"/books/{inactive}");

        var page1 = JsonDocument.Parse(await _client.GetStringAsync($"/books?search={author}&pageSize=1&page=1")).RootElement;
        Assert.Equal(2, page1.GetProperty("totalCount").GetInt32());
        Assert.Equal(2, page1.GetProperty("totalPages").GetInt32());
        Assert.Equal(1, page1.GetProperty("items").GetArrayLength());

        var all = JsonDocument.Parse(await _client.GetStringAsync($"/books?search={author}&includeInactive=true")).RootElement;
        Assert.Equal(3, all.GetProperty("totalCount").GetInt32());

        Assert.Equal(HttpStatusCode.BadRequest, (await _client.GetAsync("/books?pageSize=1000")).StatusCode);
    }
}
