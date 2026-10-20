using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using LibraryLoans.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace LibraryLoans.IntegrationTests;

public class AuditEventTests(PostgresFactory factory) : IClassFixture<PostgresFactory>
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

    private async Task<HttpResponseMessage> Send(HttpMethod method, string url, object? body, string? correlationId = null, string? actor = null)
    {
        var request = new HttpRequestMessage(method, url);
        if (body is not null) request.Content = JsonContent.Create(body);
        if (correlationId is not null) request.Headers.Add("X-Correlation-Id", correlationId);
        if (actor is not null) request.Headers.Add("X-Actor-Id", actor);
        if (url == "/loans") request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        return await _client.SendAsync(request);
    }

    private async Task<JsonElement> Events(string query) => await Json(await _client.GetAsync("/audit-events?" + query));

    private static string NewCorrelation() => "c-" + Guid.NewGuid().ToString("N");

    [PostgresFact]
    public async Task Book_lifecycle_is_audited_with_actor_correlation_and_change_data()
    {
        var correlation = NewCorrelation();
        var created = await Json(await Send(HttpMethod.Post, "/books", new { title = "Dom Casmurro", author = "Machado", isbn = NewIsbn(), totalCopies = 2 }, correlation, "bibliotecario-1"));
        var id = created.GetProperty("id").GetGuid();
        await Send(HttpMethod.Patch, $"/books/{id}", new { title = "Dom Casmurro (2ª ed.)", totalCopies = 3 }, correlation, "bibliotecario-1");
        await Send(HttpMethod.Delete, $"/books/{id}", null, correlation, "bibliotecario-1");

        var body = await Events($"entityId={id}&pageSize=10");
        var items = body.GetProperty("items").EnumerateArray().ToList();

        Assert.Equal(3, body.GetProperty("totalCount").GetInt32());
        Assert.Equal(new[] { "BookCreated", "BookDeactivated", "BookUpdated" }, items.Select(i => i.GetProperty("action").GetString()!).Order().ToArray());
        Assert.All(items, i =>
        {
            Assert.Equal("Book", i.GetProperty("entityType").GetString());
            Assert.Equal("bibliotecario-1", i.GetProperty("actorId").GetString());
            Assert.Equal(correlation, i.GetProperty("correlationId").GetString());
            Assert.True(i.GetProperty("timestampUtc").GetDateTimeOffset().Offset == TimeSpan.Zero);
        });

        var updated = items.Single(i => i.GetProperty("action").GetString() == "BookUpdated").GetProperty("data");
        Assert.Equal("Dom Casmurro", updated.GetProperty("title").GetProperty("from").GetString());
        Assert.Equal("Dom Casmurro (2ª ed.)", updated.GetProperty("title").GetProperty("to").GetString());
        Assert.Equal(3, updated.GetProperty("totalCopies").GetProperty("to").GetInt32());
    }

    [PostgresFact]
    public async Task Missing_actor_is_recorded_as_anonymous()
    {
        var created = await Json(await Send(HttpMethod.Post, "/books", new { title = "T", author = "A", isbn = NewIsbn(), totalCopies = 1 }));
        var body = await Events($"entityId={created.GetProperty("id").GetGuid()}");

        Assert.Equal("anonymous", body.GetProperty("items")[0].GetProperty("actorId").GetString());
    }

    [PostgresFact]
    public async Task Loan_creation_is_audited_in_the_same_transaction()
    {
        var book = await Json(await Send(HttpMethod.Post, "/books", new { title = "T", author = "A", isbn = NewIsbn(), totalCopies = 1 }));
        var reader = await Json(await Send(HttpMethod.Post, "/users", new { name = "Ana" }));
        var correlation = NewCorrelation();

        var loan = await Json(await Send(HttpMethod.Post, "/loans", new { bookId = book.GetProperty("id").GetGuid(), readerId = reader.GetProperty("id").GetGuid() }, correlation));

        var body = await Events($"entityType=Loan&entityId={loan.GetProperty("id").GetGuid()}");
        var evt = body.GetProperty("items")[0];
        Assert.Equal(1, body.GetProperty("totalCount").GetInt32());
        Assert.Equal("LoanCreated", evt.GetProperty("action").GetString());
        Assert.Equal(correlation, evt.GetProperty("correlationId").GetString());
        Assert.Equal(book.GetProperty("id").GetGuid(), evt.GetProperty("data").GetProperty("BookId").GetGuid());
    }

    [PostgresFact]
    public async Task Rejected_or_reverted_operations_leave_no_audit_event()
    {
        var isbn = NewIsbn();
        await Send(HttpMethod.Post, "/books", new { title = "T", author = "A", isbn, totalCopies = 1 });

        var duplicateCorrelation = NewCorrelation();
        var duplicate = await Send(HttpMethod.Post, "/books", new { title = "T2", author = "A", isbn, totalCopies = 1 }, duplicateCorrelation);
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);

        var emptyBook = await Json(await Send(HttpMethod.Post, "/books", new { title = "T", author = "A", isbn = NewIsbn(), totalCopies = 0 }));
        var reader = await Json(await Send(HttpMethod.Post, "/users", new { name = "Ana" }));
        var noStockCorrelation = NewCorrelation();
        var noStock = await Send(HttpMethod.Post, "/loans", new { bookId = emptyBook.GetProperty("id").GetGuid(), readerId = reader.GetProperty("id").GetGuid() }, noStockCorrelation);
        Assert.Equal(HttpStatusCode.Conflict, noStock.StatusCode);

        Assert.Equal(0, (await Events($"correlationId={duplicateCorrelation}")).GetProperty("totalCount").GetInt32());
        Assert.Equal(0, (await Events($"correlationId={noStockCorrelation}")).GetProperty("totalCount").GetInt32());
    }

    [PostgresFact]
    public async Task Filters_and_pagination_work_and_validate_input()
    {
        var correlation = NewCorrelation();
        for (var i = 0; i < 3; i++)
            await Send(HttpMethod.Post, "/books", new { title = "T", author = "A", isbn = NewIsbn(), totalCopies = 1 }, correlation, "ator-filtro");

        var page = await Events($"correlationId={correlation}&pageSize=2&page=2");
        Assert.Equal(3, page.GetProperty("totalCount").GetInt32());
        Assert.Equal(1, page.GetProperty("items").GetArrayLength());

        Assert.Equal(3, (await Events($"actorId=ator-filtro&action=BookCreated&entityType=Book&correlationId={correlation}")).GetProperty("totalCount").GetInt32());
        Assert.Equal(0, (await Events($"correlationId={correlation}&action=LoanCreated")).GetProperty("totalCount").GetInt32());

        var from = Uri.EscapeDataString(DateTimeOffset.UtcNow.AddMinutes(-5).ToString("O"));
        var future = Uri.EscapeDataString(DateTimeOffset.UtcNow.AddDays(1).ToString("O"));
        Assert.Equal(3, (await Events($"correlationId={correlation}&from={from}")).GetProperty("totalCount").GetInt32());
        Assert.Equal(0, (await Events($"correlationId={correlation}&from={future}")).GetProperty("totalCount").GetInt32());

        Assert.Equal(HttpStatusCode.BadRequest, (await _client.GetAsync("/audit-events?pageSize=1000")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await _client.GetAsync($"/audit-events?from={future}&to={from}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _client.GetAsync("/audit-events?entityType=Inexistente")).StatusCode);
    }

    [PostgresFact]
    public async Task Api_exposes_no_edit_or_delete_for_audit_events()
    {
        var created = await Json(await Send(HttpMethod.Post, "/books", new { title = "T", author = "A", isbn = NewIsbn(), totalCopies = 1 }));
        var evt = (await Events($"entityId={created.GetProperty("id").GetGuid()}")).GetProperty("items")[0];
        var url = $"/audit-events/{evt.GetProperty("id").GetGuid()}";

        foreach (var method in new[] { HttpMethod.Put, HttpMethod.Patch, HttpMethod.Delete, HttpMethod.Post })
            Assert.True((await _client.SendAsync(new HttpRequestMessage(method, url))).StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await _client.PostAsJsonAsync("/audit-events", new { })).StatusCode);
    }

    [PostgresFact]
    public async Task Database_rejects_update_and_delete_of_audit_events()
    {
        var created = await Json(await Send(HttpMethod.Post, "/books", new { title = "T", author = "A", isbn = NewIsbn(), totalCopies = 1 }));
        var id = created.GetProperty("id").GetGuid();

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LibraryDbContext>();

        var update = await Assert.ThrowsAsync<PostgresException>(() =>
            db.Database.ExecuteSqlInterpolatedAsync($"""UPDATE audit_events SET "Action" = 'X' WHERE "EntityId" = {id}"""));
        var delete = await Assert.ThrowsAsync<PostgresException>(() =>
            db.Database.ExecuteSqlInterpolatedAsync($"""DELETE FROM audit_events WHERE "EntityId" = {id}"""));

        Assert.Equal("P0001", update.SqlState);
        Assert.Equal("P0001", delete.SqlState);
    }
}
