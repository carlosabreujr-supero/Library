using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using LibraryLoans.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace LibraryLoans.IntegrationTests;

public class ReturnCancelLoanTests(PostgresFactory factory) : IClassFixture<PostgresFactory>
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

    private async Task<(Guid BookId, Guid LoanId)> NewLoan(int total = 1)
    {
        var b = await Json(await _client.PostAsJsonAsync("/books", new { title = "L", author = "A", isbn = NewIsbn(), totalCopies = total }));
        var r = await Json(await _client.PostAsJsonAsync("/users", new { name = "Leitor" }));
        var bookId = b.GetProperty("id").GetGuid();
        var loan = await Json(await _client.PostLoanAsync(new { bookId, readerId = r.GetProperty("id").GetGuid() }));
        return (bookId, loan.GetProperty("id").GetGuid());
    }

    private async Task<int> Available(Guid bookId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LibraryDbContext>();
        return (await db.Books.AsNoTracking().SingleAsync(b => b.Id == bookId)).AvailableCopies;
    }

    private async Task<int> AuditCount(Guid loanId, string action)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LibraryDbContext>();
        return await db.AuditEvents.CountAsync(a => a.EntityId == loanId && a.Action == action);
    }

    [PostgresTheory]
    [InlineData("return", "Returned", "returnedAtUtc", "LoanReturned")]
    [InlineData("cancel", "Cancelled", "cancelledAtUtc", "LoanCancelled")]
    public async Task Close_restores_availability_once_and_preserves_record(string op, string status, string dateField, string action)
    {
        var (bookId, loanId) = await NewLoan();
        Assert.Equal(0, await Available(bookId));

        var response = await _client.PostAsync($"/loans/{loanId}/{op}", null);
        var body = await Json(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(status, body.GetProperty("status").GetString());
        Assert.NotEqual(JsonValueKind.Null, body.GetProperty(dateField).ValueKind);
        Assert.Equal(1, await Available(bookId));
        Assert.Equal(1, await AuditCount(loanId, action));

        // Repetição e transição cruzada: 409 e contador intacto.
        Assert.Equal(HttpStatusCode.Conflict, (await _client.PostAsync($"/loans/{loanId}/{op}", null)).StatusCode);
        var other = op == "return" ? "cancel" : "return";
        Assert.Equal(HttpStatusCode.Conflict, (await _client.PostAsync($"/loans/{loanId}/{other}", null)).StatusCode);
        Assert.Equal(1, await Available(bookId));
        Assert.Equal(1, await AuditCount(loanId, action));

        // Histórico preserva o empréstimo com o novo estado e datas.
        var history = await Json(await _client.GetAsync($"/books/{bookId}/history"));
        var item = history.GetProperty("items").EnumerateArray().Single();
        Assert.Equal(status, item.GetProperty("status").GetString());
        Assert.NotEqual(JsonValueKind.Null, item.GetProperty("borrowedAtUtc").ValueKind);
    }

    [PostgresTheory]
    [InlineData("return")]
    [InlineData("cancel")]
    public async Task Unknown_loan_returns_404(string op)
    {
        var response = await _client.PostAsync($"/loans/{Guid.NewGuid()}/{op}", null);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [PostgresFact]
    public async Task Concurrent_returns_restore_exactly_once()
    {
        var (bookId, loanId) = await NewLoan();
        var other = factory.WithWebHostBuilder(_ => { }).CreateClient();

        var tasks = Enumerable.Range(0, 10)
            .Select(i => (i % 2 == 0 ? _client : other).PostAsync($"/loans/{loanId}/return", null))
            .ToArray();
        var results = await Task.WhenAll(tasks);

        Assert.Equal(1, results.Count(r => r.StatusCode == HttpStatusCode.OK));
        Assert.Equal(9, results.Count(r => r.StatusCode == HttpStatusCode.Conflict));
        Assert.Equal(1, await Available(bookId));
        Assert.Equal(1, await AuditCount(loanId, "LoanReturned"));
    }

    [PostgresFact]
    public async Task Concurrent_return_and_cancel_restore_exactly_once()
    {
        var (bookId, loanId) = await NewLoan();

        var results = await Task.WhenAll(
            _client.PostAsync($"/loans/{loanId}/return", null),
            _client.PostAsync($"/loans/{loanId}/cancel", null));

        Assert.Equal(1, results.Count(r => r.StatusCode == HttpStatusCode.OK));
        Assert.Equal(1, results.Count(r => r.StatusCode == HttpStatusCode.Conflict));
        Assert.Equal(1, await Available(bookId));
    }

    [PostgresFact]
    public async Task Book_can_be_borrowed_again_after_return()
    {
        var (bookId, loanId) = await NewLoan();
        await _client.PostAsync($"/loans/{loanId}/return", null);

        var reader = await Json(await _client.PostAsJsonAsync("/users", new { name = "Outro" }));
        var again = await _client.PostLoanAsync(new { bookId, readerId = reader.GetProperty("id").GetGuid() });

        Assert.Equal(HttpStatusCode.Created, again.StatusCode);
        Assert.Equal(0, await Available(bookId));
    }
}
