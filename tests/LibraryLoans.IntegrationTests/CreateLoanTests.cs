using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using LibraryLoans.Application.Abstractions;
using LibraryLoans.Domain.Entities;
using LibraryLoans.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace LibraryLoans.IntegrationTests;

public class CreateLoanTests(PostgresFactory factory) : IClassFixture<PostgresFactory>
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

    private async Task<Guid> CreateBook(int total)
    {
        var r = await _client.PostAsJsonAsync("/books", new { title = "L", author = "A", isbn = NewIsbn(), totalCopies = total });
        return (await Json(r)).GetProperty("id").GetGuid();
    }

    private async Task<Guid> CreateReader()
    {
        var r = await _client.PostAsJsonAsync("/users", new { name = "Leitor" });
        return (await Json(r)).GetProperty("id").GetGuid();
    }

    private async Task<(int Available, int Loans)> State(Guid bookId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LibraryDbContext>();
        var book = await db.Books.AsNoTracking().SingleAsync(b => b.Id == bookId);
        return (book.AvailableCopies, await db.Loans.CountAsync(l => l.BookId == bookId));
    }

    [PostgresFact]
    public async Task Create_loan_returns_201_reserves_copy_and_audits()
    {
        var bookId = await CreateBook(2);
        var readerId = await CreateReader();

        var response = await _client.PostAsJsonAsync("/loans", new { bookId, readerId });
        var body = await Json(response);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal("Active", body.GetProperty("status").GetString());
        var borrowed = body.GetProperty("borrowedAtUtc").GetDateTime();
        var due = body.GetProperty("dueAtUtc").GetDateTime();
        Assert.Equal(TimeSpan.FromDays(14), due - borrowed);
        Assert.Equal((1, 1), await State(bookId));

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LibraryDbContext>();
        Assert.Equal(1, await db.AuditEvents.CountAsync(a => a.EntityId == body.GetProperty("id").GetGuid() && a.Action == "LoanCreated"));
    }

    [PostgresFact]
    public async Task Last_copy_under_concurrency_is_reserved_exactly_once_across_replicas()
    {
        var bookId = await CreateBook(1);
        var readerId = await CreateReader();

        // Duas instâncias da API simulam réplicas compartilhando o mesmo PostgreSQL.
        using var replicaB = new PostgresFactory();
        var clients = new[] { _client, replicaB.CreateClient() };

        var responses = await Task.WhenAll(Enumerable.Range(0, 40).Select(i =>
            clients[i % 2].PostAsJsonAsync("/loans", new { bookId, readerId })));

        Assert.Equal(1, responses.Count(r => r.StatusCode == HttpStatusCode.Created));
        Assert.Equal(39, responses.Count(r => r.StatusCode == HttpStatusCode.Conflict));
        Assert.Equal((0, 1), await State(bookId));
    }

    [PostgresFact]
    public async Task Stock_never_goes_negative_with_multiple_copies()
    {
        var bookId = await CreateBook(5);
        var readerId = await CreateReader();

        var responses = await Task.WhenAll(Enumerable.Range(0, 25).Select(_ =>
            _client.PostAsJsonAsync("/loans", new { bookId, readerId })));

        Assert.Equal(5, responses.Count(r => r.StatusCode == HttpStatusCode.Created));
        Assert.Equal((0, 5), await State(bookId));
    }

    [PostgresFact]
    public async Task No_availability_returns_409_without_creating_loan()
    {
        var bookId = await CreateBook(0);
        var readerId = await CreateReader();

        var response = await _client.PostAsJsonAsync("/loans", new { bookId, readerId });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal((0, 0), await State(bookId));
    }

    [PostgresFact]
    public async Task Deactivated_book_returns_409()
    {
        var bookId = await CreateBook(3);
        var readerId = await CreateReader();
        await _client.DeleteAsync($"/books/{bookId}");

        var response = await _client.PostAsJsonAsync("/loans", new { bookId, readerId });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal((3, 0), await State(bookId));
    }

    [PostgresFact]
    public async Task Inactive_reader_returns_409()
    {
        var bookId = await CreateBook(1);
        var readerId = await CreateReader();
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LibraryDbContext>();
            await db.Readers.Where(r => r.Id == readerId).ExecuteUpdateAsync(s => s.SetProperty(r => r.IsActive, false));
        }

        var response = await _client.PostAsJsonAsync("/loans", new { bookId, readerId });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal((1, 0), await State(bookId));
    }

    [PostgresFact]
    public async Task Unknown_ids_return_404_and_missing_ids_return_400()
    {
        var bookId = await CreateBook(1);
        var readerId = await CreateReader();

        Assert.Equal(HttpStatusCode.NotFound, (await _client.PostAsJsonAsync("/loans", new { bookId = Guid.NewGuid(), readerId })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.PostAsJsonAsync("/loans", new { bookId, readerId = Guid.NewGuid() })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await _client.PostAsJsonAsync("/loans", new { bookId })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await _client.PostAsJsonAsync("/loans", new { })).StatusCode);
        Assert.Equal((1, 0), await State(bookId));
    }

    [PostgresFact]
    public async Task Failure_after_reservation_rolls_back_the_whole_transaction()
    {
        var bookId = await CreateBook(1);
        var readerId = await CreateReader();

        using var failing = factory.WithWebHostBuilder(b => b.ConfigureServices(s =>
            s.Replace(ServiceDescriptor.Scoped<IAuditTrail, ThrowingAuditTrail>())));
        var response = await failing.CreateClient().PostAsJsonAsync("/loans", new { bookId, readerId });

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal((1, 0), await State(bookId));

        var retry = await _client.PostAsJsonAsync("/loans", new { bookId, readerId });
        Assert.Equal(HttpStatusCode.Created, retry.StatusCode);
    }

    private sealed class ThrowingAuditTrail : IAuditTrail
    {
        public void Record(string entityType, Guid entityId, string action, object? data) =>
            throw new InvalidOperationException("falha simulada");
    }
}
