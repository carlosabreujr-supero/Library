using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LibraryLoans.Application.Abstractions;
using LibraryLoans.Application.Audit;
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

public sealed class IdempotencySettings
{
    public int RetentionHours { get; set; } = 24;
    public int PurgeIntervalMinutes { get; set; } = 60;

    public void Validate()
    {
        if (RetentionHours < 1) throw new InvalidOperationException("Idempotency:RetentionHours deve ser >= 1.");
        if (PurgeIntervalMinutes < 1) throw new InvalidOperationException("Idempotency:PurgeIntervalMinutes deve ser >= 1.");
    }
}

public sealed record CreateLoanCommand(Guid? BookId, Guid? ReaderId, string? IdempotencyKey);

// Corpo já serializado: em repetições, é exatamente o que foi persistido na primeira execução.
public sealed record CreateLoanResult(int StatusCode, string Body, bool Replayed);

public sealed class CreateLoanHandler(
    IBookRepository books,
    IReaderRepository readers,
    ILoanRepository loans,
    IIdempotencyRepository idempotency,
    IAuditTrail audit,
    IUnitOfWork unitOfWork,
    IReadCache cache,
    LoanSettings settings,
    TimeProvider clock)
{
    public const int MaxKeyLength = 200;
    private const int CreatedStatusCode = 201;

    public async Task<CreateLoanResult> HandleAsync(CreateLoanCommand command, CancellationToken ct)
    {
        var errors = new Dictionary<string, string[]>();
        var key = command.IdempotencyKey?.Trim();
        if (string.IsNullOrEmpty(key))
            errors["Idempotency-Key"] = ["O cabeçalho Idempotency-Key é obrigatório."];
        else if (key.Length > MaxKeyLength || !key.All(c => c is > ' ' and < (char)127))
            errors["Idempotency-Key"] = [$"Idempotency-Key deve ter até {MaxKeyLength} caracteres ASCII imprimíveis, sem espaços."];

        if (command.BookId is null || command.BookId == Guid.Empty) errors["bookId"] = ["bookId é obrigatório."];
        if (command.ReaderId is null || command.ReaderId == Guid.Empty) errors["readerId"] = ["readerId é obrigatório."];
        if (errors.Count > 0) throw new ValidationException(errors);

        var bookId = command.BookId!.Value;
        var readerId = command.ReaderId!.Value;
        var requestHash = ComputeHash(bookId, readerId);

        var result = await unitOfWork.ExecuteInTransactionAsync(async token =>
        {
            var now = clock.GetUtcNow().UtcDateTime;

            // Passo 1: adquire a chave. Requisições concorrentes com a mesma chave esperam aqui pelo commit/rollback.
            if (!await idempotency.TryBeginAsync(key!, requestHash, now, token))
            {
                var existing = await idempotency.GetAsync(key!, token)
                    ?? throw new ConflictException("Conflito ao processar a Idempotency-Key. Tente novamente.");

                if (existing.RequestHash != requestHash)
                    throw new ConflictException("Idempotency-Key já utilizada com um payload diferente.");

                if (existing.Status != IdempotencyStatus.Completed || existing.ResponseBody is null)
                    throw new ConflictException("Requisição com esta Idempotency-Key ainda está em processamento.");

                return new CreateLoanResult(existing.ResponseStatusCode ?? CreatedStatusCode, existing.ResponseBody, Replayed: true);
            }

            var reader = await readers.GetAsNoTrackingAsync(readerId, token)
                ?? throw new NotFoundException($"Leitor {readerId} não encontrado.");
            if (!reader.IsActive)
                throw new ConflictException("Leitor inativo não pode realizar empréstimos.");

            var book = await books.GetAsNoTrackingAsync(bookId, token)
                ?? throw new NotFoundException($"Livro {bookId} não encontrado.");
            if (!book.IsActive)
                throw new ConflictException("Livro desativado não pode ser emprestado.");

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
            audit.Record(nameof(Loan), loan.Id, AuditActions.LoanCreated, new
            {
                loan.BookId, loan.ReaderId, loan.BorrowedAtUtc, loan.DueAtUtc
            });
            await unitOfWork.SaveChangesAsync(token);

            // Resultado persistido na mesma transação: ou tudo é confirmado, ou nada (inclusive a chave).
            var body = JsonSerializer.Serialize(LoanDto.From(loan), JsonSerializerOptions.Web);
            await idempotency.CompleteAsync(key!, loan.Id, CreatedStatusCode, body, token);

            return new CreateLoanResult(CreatedStatusCode, body, Replayed: false);
        }, ct);

        // Invalida somente após o commit; repetição idempotente não altera estoque.
        if (!result.Replayed)
            await cache.InvalidateAsync(CacheAreas.ForBook(bookId));

        return result;
    }

    // Hash dos campos relevantes em forma canônica (independe de formatação/ordem do JSON).
    private static string ComputeHash(Guid bookId, Guid readerId) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"loan:v1|{bookId:N}|{readerId:N}")));
}
