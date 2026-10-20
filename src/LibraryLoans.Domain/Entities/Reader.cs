namespace LibraryLoans.Domain.Entities;

public class Reader
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAtUtc { get; set; }

    public ICollection<Loan> Loans { get; set; } = [];
}
