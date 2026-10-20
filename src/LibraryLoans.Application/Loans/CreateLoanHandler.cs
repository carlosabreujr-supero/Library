using LibraryLoans.Application.Abstractions;
using LibraryLoans.Application.Exceptions;
using LibraryLoans.Domain.Entities;

namespace LibraryLoans.Application.Loans;

public sealed class LoanSettings
{
    public const int MaxLoanDays = 365;

    public int DefaultLoanDays { get; set; } = 14;

    public void Validate()
    {
        if (DefaultLoanDays is < 1 or > MaxLoanDays)
            throw new InvalidOperationException($"Loans:DefaultLoanDays deve estar entre 1 e {MaxLoanDays}.");
    }
}

public sealed record CreateLoanCommand(Guid? BookId, Guid? ReaderId);

public sealed class CreateLoanHandler(
    IBookRepository books,
    IReaderRepository readers,
    ILoanRepository loans,
    IAuditTrail audit,
    IUnitOfWork unitOfWork,
    LoanSettings settings,
    TimeProvider clock)
{
    public async Task<LoanDto> HandleAsync(CreateLoanCommand command, CancellationToken ct)
    {
        var errors = new Dictionary<string, string[]>();
        if (command.BookId is null || command.BookId == Guid.Empty) errors["bookId"] = ["bookId é obrigatório."];
        if (command.ReaderId is null || command.ReaderId == Guid.Empty) errors["readerId"] = ["readerId é obrigatório."];
        if (errors.Count > 0) throw new ValidationException(errors);

        var bookId = command.BookId!.Value;
        var readerId = command.ReaderId!.Value;

        var reader = await readers.GetAsNoTrackingAsync(readerId, ct)
            ?? throw new NotFoundException($"Leitor {readerId} não encontrado.");
        if (!reader.IsActive)
            throw new ConflictException("Leitor inativo não pode realizar empréstimos.");

        var book = await books.GetAsNoTrackingAsync(bookId, ct)
            ?? throw new NotFoundException($"Livro {bookId} não encontrado.");
        if (!book.IsActive)
            throw new ConflictException("Livro desativado não pode ser emprestado.");

        return await unitOfWork.ExecuteInTransactionAsync(async token =>
        {
            var now = clock.GetUtcNow().UtcDateTime;

            // A disponibilidade é decidida apenas por esta atualização atômica no PostgreSQL.
            if (!await books.TryReserveCopyAsync(bookId, now, token))
                throw new ConflictException("Não há exemplares disponíveis para este livro.");

            var loan = new Loan
            {
                Id = Guid.NewGuid(),
                BookId = bookId,
                ReaderId = readerId,
                BorrowedAtUtc = now,
                DueAtUtc = now.AddDays(settings.DefaultLoanDays),
                Status = LoanStatus.Active
            };

            loans.Add(loan);
            audit.Record(nameof(Loan), loan.Id, "LoanCreated", new
            {
                loan.BookId, loan.ReaderId, loan.BorrowedAtUtc, loan.DueAtUtc
            });
            await unitOfWork.SaveChangesAsync(token);

            return LoanDto.From(loan);
        }, ct);
    }
}
