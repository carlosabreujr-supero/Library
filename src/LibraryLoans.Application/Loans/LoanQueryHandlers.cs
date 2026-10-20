using LibraryLoans.Application.Abstractions;
using LibraryLoans.Application.Common;
using LibraryLoans.Application.Exceptions;
using LibraryLoans.Domain.Entities;

namespace LibraryLoans.Application.Loans;

internal static class HistoryValidation
{
    public const int MaxPageSize = 100;

    // Sem filtro retorna todos os status (ativos, devolvidos e cancelados).
    public static LoanStatus? Validate(LoanHistoryQuery query)
    {
        var errors = new Dictionary<string, string[]>();
        if (query.Page < 1) errors["page"] = ["page deve ser maior ou igual a 1."];
        if (query.PageSize is < 1 or > MaxPageSize) errors["pageSize"] = [$"pageSize deve estar entre 1 e {MaxPageSize}."];

        LoanStatus? status = null;
        if (!string.IsNullOrWhiteSpace(query.Status))
        {
            if (Enum.TryParse<LoanStatus>(query.Status, ignoreCase: true, out var parsed) && Enum.IsDefined(parsed))
                status = parsed;
            else
                errors["status"] = ["status deve ser Active, Returned ou Cancelled."];
        }

        if (errors.Count > 0) throw new ValidationException(errors);
        return status;
    }
}

public sealed class GetReaderLoansHandler(IReaderRepository readers, ILoanQueries loans)
{
    public async Task<PagedResult<LoanDto>> HandleAsync(LoanHistoryQuery query, CancellationToken ct)
    {
        var status = HistoryValidation.Validate(query);

        if (!await readers.ExistsAsync(query.Id, ct))
            throw new NotFoundException($"Leitor {query.Id} não encontrado.");

        var result = await loans.ListByReaderAsync(query.Id, status, query.Page, query.PageSize, ct);
        return new PagedResult<LoanDto>(result.Items.Select(LoanDto.From).ToList(), result.Page, result.PageSize, result.TotalCount);
    }
}

public sealed class GetBookHistoryHandler(IBookRepository books, ILoanQueries loans)
{
    public async Task<PagedResult<LoanDto>> HandleAsync(LoanHistoryQuery query, CancellationToken ct)
    {
        var status = HistoryValidation.Validate(query);

        if (!await books.ExistsAsync(query.Id, ct))
            throw new NotFoundException($"Livro {query.Id} não encontrado.");

        var result = await loans.ListByBookAsync(query.Id, status, query.Page, query.PageSize, ct);
        return new PagedResult<LoanDto>(result.Items.Select(LoanDto.From).ToList(), result.Page, result.PageSize, result.TotalCount);
    }
}
