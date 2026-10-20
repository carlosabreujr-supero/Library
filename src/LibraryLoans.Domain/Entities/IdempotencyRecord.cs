namespace LibraryLoans.Domain.Entities;

public enum IdempotencyStatus
{
    InProgress,
    Completed
}

// A chave do cabeçalho Idempotency-Key é única globalmente e é a chave primária.
public class IdempotencyRecord
{
    public string Key { get; set; } = string.Empty;
    public string RequestHash { get; set; } = string.Empty;
    public IdempotencyStatus Status { get; set; } = IdempotencyStatus.InProgress;
    public int? ResponseStatusCode { get; set; }
    public string? ResponseBody { get; set; }
    public Guid? LoanId { get; set; }
    public DateTime CreatedAtUtc { get; set; }

    public Loan? Loan { get; set; }
}
