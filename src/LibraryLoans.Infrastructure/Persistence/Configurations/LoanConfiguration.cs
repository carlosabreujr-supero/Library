using LibraryLoans.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace LibraryLoans.Infrastructure.Persistence.Configurations;

public class LoanConfiguration : IEntityTypeConfiguration<Loan>
{
    public void Configure(EntityTypeBuilder<Loan> builder)
    {
        builder.ToTable("loans", t =>
        {
            t.HasCheckConstraint("ck_loans_due_after_borrowed", "\"DueAtUtc\" > \"BorrowedAtUtc\"");
            t.HasCheckConstraint("ck_loans_status_dates",
                "(\"Status\" = 'Active' AND \"ReturnedAtUtc\" IS NULL AND \"CancelledAtUtc\" IS NULL) " +
                "OR (\"Status\" = 'Returned' AND \"ReturnedAtUtc\" IS NOT NULL AND \"CancelledAtUtc\" IS NULL) " +
                "OR (\"Status\" = 'Cancelled' AND \"CancelledAtUtc\" IS NOT NULL AND \"ReturnedAtUtc\" IS NULL)");
        });

        builder.HasKey(l => l.Id);
        builder.Property(l => l.Id).ValueGeneratedNever();
        builder.Property(l => l.Status).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(l => l.BorrowedAtUtc).HasColumnType("timestamp with time zone");
        builder.Property(l => l.DueAtUtc).HasColumnType("timestamp with time zone");
        builder.Property(l => l.ReturnedAtUtc).HasColumnType("timestamp with time zone");
        builder.Property(l => l.CancelledAtUtc).HasColumnType("timestamp with time zone");

        // Restrict: histórico de empréstimos nunca é removido em cascata.
        builder.HasOne(l => l.Book).WithMany(b => b.Loans).HasForeignKey(l => l.BookId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(l => l.Reader).WithMany(r => r.Loans).HasForeignKey(l => l.ReaderId).OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(l => new { l.ReaderId, l.Status }).HasDatabaseName("ix_loans_reader_status");
        builder.HasIndex(l => new { l.BookId, l.Status }).HasDatabaseName("ix_loans_book_status");
    }
}
