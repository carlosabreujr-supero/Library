using LibraryLoans.Application.Abstractions;
using LibraryLoans.Application.Audit;
using LibraryLoans.Application.Exceptions;
using LibraryLoans.Domain.Entities;
using Microsoft.Extensions.Logging;

namespace LibraryLoans.Application.Loans;

public sealed record ReturnLoanCommand(Guid Id);
public sealed record CancelLoanCommand(Guid Id);

public sealed class ReturnLoanHandler(CloseLoanService service)
{
    public Task<LoanDto> HandleAsync(ReturnLoanCommand command, CancellationToken ct) =>
        service.CloseAsync(command.Id, LoanStatus.Returned, AuditActions.LoanReturned, ct);
}

public sealed class CancelLoanHandler(CloseLoanService service)
{
    public Task<LoanDto> HandleAsync(CancelLoanCommand command, CancellationToken ct) =>
        service.CloseAsync(command.Id, LoanStatus.Cancelled, AuditActions.LoanCancelled, ct);
}

// Devolução e cancelamento: transição condicional + restauração do contador + auditoria em uma única transação.
public sealed class CloseLoanService(
    IBookRepository books,
    ILoanRepository loans,
    IAuditTrail audit,
    IUnitOfWork unitOfWork,
    IReadCache cache,
    TimeProvider clock,
    ILogger<CloseLoanService> logger)
{
    public async Task<LoanDto> CloseAsync(Guid id, LoanStatus target, string auditAction, CancellationToken ct)
    {
        if (id == Guid.Empty) throw new NotFoundException($"Empréstimo {id} não encontrado.");

        using var activity = LoanTelemetry.Source.StartActivity($"Loan.{target}");
        activity?.SetTag("library.loan_id", id);
        var dto = await unitOfWork.ExecuteInTransactionAsync(async token =>
        {
            var now = clock.GetUtcNow().UtcDateTime;

            var bookId = await loans.TryCloseAsync(id, target, now, token);
            if (bookId is null)
            {
                var current = await loans.GetAsNoTrackingAsync(id, token)
                    ?? throw new NotFoundException($"Empréstimo {id} não encontrado.");
                throw new ConflictException(
                    $"Transição inválida: o empréstimo está '{current.Status}' e só empréstimos ativos podem ser alterados.");
            }

            // Só chega aqui a transação que venceu a transição, então o contador é restaurado exatamente uma vez.
            if (!await books.ReleaseCopyAsync(bookId.Value, now, token))
                throw new InvalidOperationException($"Inconsistência de estoque ao restaurar o livro {bookId}.");

            var loan = await loans.GetAsNoTrackingAsync(id, token)
                ?? throw new InvalidOperationException("Empréstimo não encontrado após a transição.");

            audit.Record(nameof(Loan), id, auditAction, new
            {
                loan.BookId,
                loan.ReaderId,
                PreviousStatus = LoanStatus.Active.ToString(),
                NewStatus = target.ToString(),
                loan.BorrowedAtUtc,
                loan.ReturnedAtUtc,
                loan.CancelledAtUtc
            });
            await unitOfWork.SaveChangesAsync(token);

            return LoanDto.From(loan);
        }, ct);

        await cache.InvalidateAsync(CacheAreas.ForBook(dto.BookId));
        LoanTelemetry.LoansClosed.Add(1, new KeyValuePair<string, object?>("status", target.ToString()));
        logger.LogInformation("Empréstimo {Status}. LoanId={LoanId} BookId={BookId}", target, id, dto.BookId);
        return dto;
    }
}
