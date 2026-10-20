using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using LibraryLoans.Application.Abstractions;
using LibraryLoans.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace LibraryLoans.IntegrationTests;

public class LoanIdempotencyTests(PostgresFactory factory) : IClassFixture<PostgresFactory>
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

    private async Task<bool> KeyExists(string key)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LibraryDbContext>();
        return await db.IdempotencyRecords.AnyAsync(i => i.Key == key);
    }

    private static string NewKey() => "key-" + Guid.NewGuid().ToString("N");

    [PostgresFact]
    public async Task Missing_or_invalid_key_returns_400_and_changes_nothing()
    {
        var bookId = await CreateBook(1);
        var readerId = await CreateReader();

        var missing = await _client.PostAsJsonAsync("/loans", new { bookId, readerId });
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
        Assert.True((await Json(missing)).GetProperty("errors").TryGetProperty("Idempotency-Key", out _));

        var tooLong = await _client.PostLoanAsync(new { bookId, readerId }, new string('k', 201));
        Assert.Equal(HttpStatusCode.BadRequest, tooLong.StatusCode);

        Assert.Equal((1, 0), await State(bookId));
    }

    [PostgresFact]
    public async Task Same_key_and_payload_returns_previous_result_without_new_loan_or_decrement()
    {
        var bookId = await CreateBook(3);
        var readerId = await CreateReader();
        var key = NewKey();

        var first = await _client.PostLoanAsync(new { bookId, readerId }, key);
        var firstBody = await first.Content.ReadAsStringAsync();
        var second = await _client.PostLoanAsync(new { readerId, bookId }, key); // ordem dos campos diferente
        var secondBody = await second.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Created, second.StatusCode);
        Assert.False(first.Headers.Contains("Idempotent-Replayed"));
        Assert.Equal("true", second.Headers.GetValues("Idempotent-Replayed").Single());
        Assert.Equal(firstBody, secondBody);
        Assert.Equal((2, 1), await State(bookId));
    }

    [PostgresFact]
    public async Task Replay_still_returns_original_result_after_book_is_deactivated()
    {
        var bookId = await CreateBook(2);
        var readerId = await CreateReader();
        var key = NewKey();
        var first = await _client.PostLoanAsync(new { bookId, readerId }, key);
        await _client.DeleteAsync($"/books/{bookId}");

        var replay = await _client.PostLoanAsync(new { bookId, readerId }, key);

        Assert.Equal(HttpStatusCode.Created, replay.StatusCode);
        Assert.Equal(await first.Content.ReadAsStringAsync(), await replay.Content.ReadAsStringAsync());
    }

    [PostgresFact]
    public async Task Same_key_with_different_payload_returns_409_without_side_effects()
    {
        var bookA = await CreateBook(2);
        var bookB = await CreateBook(2);
        var readerId = await CreateReader();
        var key = NewKey();
        Assert.Equal(HttpStatusCode.Created, (await _client.PostLoanAsync(new { bookId = bookA, readerId }, key)).StatusCode);

        var response = await _client.PostLoanAsync(new { bookId = bookB, readerId }, key);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal((2, 0), await State(bookB));
        Assert.Equal((1, 1), await State(bookA));
    }

    [PostgresFact]
    public async Task Concurrent_requests_with_same_key_yield_single_business_result_across_replicas()
    {
        var bookId = await CreateBook(5);
        var readerId = await CreateReader();
        var key = NewKey();
        using var replicaB = new PostgresFactory();
        var clients = new[] { _client, replicaB.CreateClient() };

        var responses = await Task.WhenAll(Enumerable.Range(0, 30).Select(i =>
            clients[i % 2].PostLoanAsync(new { bookId, readerId }, key)));
        var bodies = await Task.WhenAll(responses.Select(r => r.Content.ReadAsStringAsync()));

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.Created, r.StatusCode));
        Assert.Single(bodies.Distinct());
        Assert.Equal(1, responses.Count(r => !r.Headers.Contains("Idempotent-Replayed")));
        Assert.Equal((4, 1), await State(bookId));
    }

    [PostgresFact]
    public async Task Business_failure_does_not_leave_a_permanent_key_and_retry_can_succeed()
    {
        var bookId = await CreateBook(0);
        var readerId = await CreateReader();
        var key = NewKey();

        var failed = await _client.PostLoanAsync(new { bookId, readerId }, key);
        Assert.Equal(HttpStatusCode.Conflict, failed.StatusCode);
        Assert.False(await KeyExists(key));

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LibraryDbContext>();
            await db.Database.ExecuteSqlInterpolatedAsync($"""UPDATE books SET "TotalCopies" = 1, "AvailableCopies" = 1 WHERE "Id" = {bookId}""");
        }

        Assert.Equal(HttpStatusCode.Created, (await _client.PostLoanAsync(new { bookId, readerId }, key)).StatusCode);
        Assert.Equal((0, 1), await State(bookId));
    }

    [PostgresFact]
    public async Task Unexpected_failure_rolls_back_reservation_and_key()
    {
        var bookId = await CreateBook(1);
        var readerId = await CreateReader();
        var key = NewKey();

        using var failing = factory.WithWebHostBuilder(b => b.ConfigureServices(s =>
            s.Replace(ServiceDescriptor.Scoped<IAuditTrail, ThrowingAuditTrail>())));
        var response = await failing.CreateClient().PostLoanAsync(new { bookId, readerId }, key);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal((1, 0), await State(bookId));
        Assert.False(await KeyExists(key));
        Assert.Equal(HttpStatusCode.Created, (await _client.PostLoanAsync(new { bookId, readerId }, key)).StatusCode);
    }

    [PostgresFact]
    public async Task Persisted_record_is_completed_with_response_and_loan()
    {
        var bookId = await CreateBook(1);
        var readerId = await CreateReader();
        var key = NewKey();
        var body = await Json(await _client.PostLoanAsync(new { bookId, readerId }, key));

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LibraryDbContext>();
        var record = await db.IdempotencyRecords.AsNoTracking().SingleAsync(i => i.Key == key);

        Assert.Equal(Domain.Entities.IdempotencyStatus.Completed, record.Status);
        Assert.Equal(201, record.ResponseStatusCode);
        Assert.Equal(body.GetProperty("id").GetGuid(), record.LoanId);
        Assert.False(string.IsNullOrEmpty(record.RequestHash));
    }

    private sealed class ThrowingAuditTrail : IAuditTrail
    {
        public void Record(string entityType, Guid entityId, string action, object? data) =>
            throw new InvalidOperationException("falha simulada");
    }
}
