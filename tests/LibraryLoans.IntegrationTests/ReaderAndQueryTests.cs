using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using LibraryLoans.Domain.Entities;
using LibraryLoans.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace LibraryLoans.IntegrationTests;

public class ReaderAndQueryTests(PostgresFactory factory) : IClassFixture<PostgresFactory>
{
    private readonly HttpClient _client = factory.CreateClient();

    private static string NewIsbn()
    {
        var digits = "978" + Random.Shared.NextInt64(0, 1_000_000_000).ToString("D9");
        var sum = digits.Select((c, i) => (c - '0') * (i % 2 == 0 ? 1 : 3)).Sum();
        return digits + (10 - sum % 10) % 10;
    }

    private async Task<JsonElement> Json(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();

    private async Task<Guid> CreateBook(int total = 2)
    {
        var r = await _client.PostAsJsonAsync("/books", new { title = "L", author = "A", isbn = NewIsbn(), totalCopies = total });
        return (await Json(r)).GetProperty("id").GetGuid();
    }

    private async Task<Guid> CreateReader()
    {
        var r = await _client.PostAsJsonAsync("/users", new { name = "Maria" });
        return (await Json(r)).GetProperty("id").GetGuid();
    }

    // Os endpoints de empréstimo ainda não existem; insere diretamente, como o banco os teria.
    private async Task SeedLoans(Guid bookId, Guid readerId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LibraryDbContext>();
        var t = DateTime.UtcNow.AddDays(-10);
        db.Loans.AddRange(
            new Loan { Id = Guid.NewGuid(), BookId = bookId, ReaderId = readerId, BorrowedAtUtc = t, DueAtUtc = t.AddDays(14), Status = LoanStatus.Active },
            new Loan { Id = Guid.NewGuid(), BookId = bookId, ReaderId = readerId, BorrowedAtUtc = t.AddDays(1), DueAtUtc = t.AddDays(15), Status = LoanStatus.Returned, ReturnedAtUtc = t.AddDays(3) },
            new Loan { Id = Guid.NewGuid(), BookId = bookId, ReaderId = readerId, BorrowedAtUtc = t.AddDays(2), DueAtUtc = t.AddDays(16), Status = LoanStatus.Cancelled, CancelledAtUtc = t.AddDays(2) });
        await db.SaveChangesAsync();
    }

    [PostgresFact]
    public async Task Create_reader_returns_201_active()
    {
        var response = await _client.PostAsJsonAsync("/users", new { name = "  João  " });
        var body = await Json(response);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal("João", body.GetProperty("name").GetString());
        Assert.True(body.GetProperty("isActive").GetBoolean());
        Assert.EndsWith($"/users/{body.GetProperty("id").GetGuid()}", response.Headers.Location!.ToString());
    }

    [PostgresFact]
    public async Task Create_reader_with_invalid_name_returns_400()
    {
        foreach (var name in new[] { "", "   ", new string('x', 201) })
        {
            var response = await _client.PostAsJsonAsync("/users", new { name });
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.True((await Json(response)).GetProperty("errors").TryGetProperty("name", out _));
        }
    }

    [PostgresFact]
    public async Task Reader_without_loans_returns_empty_page()
    {
        var readerId = await CreateReader();

        var response = await _client.GetAsync($"/users/{readerId}/loans");
        var body = await Json(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(0, body.GetProperty("items").GetArrayLength());
        Assert.Equal(0, body.GetProperty("totalCount").GetInt32());
    }

    [PostgresFact]
    public async Task Book_without_loans_returns_empty_history()
    {
        var bookId = await CreateBook();

        var body = await Json(await _client.GetAsync($"/books/{bookId}/history"));

        Assert.Equal(0, body.GetProperty("items").GetArrayLength());
    }

    [PostgresFact]
    public async Task Unknown_ids_return_404()
    {
        var id = Guid.NewGuid();
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync($"/users/{id}/loans")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync($"/books/{id}/history")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync($"/books/{id}/availability")).StatusCode);
    }

    [PostgresFact]
    public async Task History_includes_active_returned_and_cancelled_for_reader_and_book()
    {
        var bookId = await CreateBook();
        var readerId = await CreateReader();
        await SeedLoans(bookId, readerId);

        foreach (var url in new[] { $"/users/{readerId}/loans", $"/books/{bookId}/history" })
        {
            var body = await Json(await _client.GetAsync(url));
            var statuses = body.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("status").GetString()).ToHashSet();
            Assert.Equal(3, body.GetProperty("totalCount").GetInt32());
            Assert.Equal(new HashSet<string?> { "Active", "Returned", "Cancelled" }, statuses);
        }
    }

    [PostgresFact]
    public async Task History_supports_pagination_and_status_filter()
    {
        var bookId = await CreateBook();
        var readerId = await CreateReader();
        await SeedLoans(bookId, readerId);

        var page = await Json(await _client.GetAsync($"/users/{readerId}/loans?pageSize=2&page=2"));
        Assert.Equal(1, page.GetProperty("items").GetArrayLength());
        Assert.Equal(2, page.GetProperty("totalPages").GetInt32());

        var cancelled = await Json(await _client.GetAsync($"/books/{bookId}/history?status=cancelled"));
        Assert.Equal(1, cancelled.GetProperty("totalCount").GetInt32());
        Assert.NotEqual(JsonValueKind.Null, cancelled.GetProperty("items")[0].GetProperty("cancelledAtUtc").ValueKind);

        Assert.Equal(HttpStatusCode.BadRequest, (await _client.GetAsync($"/books/{bookId}/history?status=xyz")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await _client.GetAsync($"/users/{readerId}/loans?pageSize=0")).StatusCode);
    }

    [PostgresFact]
    public async Task Availability_reflects_database_state()
    {
        var bookId = await CreateBook(total: 2);

        var initial = await Json(await _client.GetAsync($"/books/{bookId}/availability"));
        Assert.Equal(2, initial.GetProperty("availableCopies").GetInt32());
        Assert.True(initial.GetProperty("isAvailable").GetBoolean());

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LibraryDbContext>();
            var book = await db.Books.FindAsync(bookId);
            book!.AvailableCopies = 0;
            await db.SaveChangesAsync();
        }

        var exhausted = await Json(await _client.GetAsync($"/books/{bookId}/availability"));
        Assert.Equal(0, exhausted.GetProperty("availableCopies").GetInt32());
        Assert.Equal(2, exhausted.GetProperty("borrowedCopies").GetInt32());
        Assert.False(exhausted.GetProperty("isAvailable").GetBoolean());

        await _client.DeleteAsync($"/books/{bookId}");
        var inactive = await Json(await _client.GetAsync($"/books/{bookId}/availability"));
        Assert.False(inactive.GetProperty("isActive").GetBoolean());
    }

    [PostgresFact]
    public async Task Deactivated_book_with_stock_is_not_available()
    {
        var bookId = await CreateBook(total: 3);
        await _client.DeleteAsync($"/books/{bookId}");

        var body = await Json(await _client.GetAsync($"/books/{bookId}/availability"));

        Assert.Equal(3, body.GetProperty("availableCopies").GetInt32());
        Assert.False(body.GetProperty("isAvailable").GetBoolean());
    }
}
