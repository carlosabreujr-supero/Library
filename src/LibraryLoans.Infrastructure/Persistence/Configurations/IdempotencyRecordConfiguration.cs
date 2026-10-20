using LibraryLoans.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace LibraryLoans.Infrastructure.Persistence.Configurations;

public class IdempotencyRecordConfiguration : IEntityTypeConfiguration<IdempotencyRecord>
{
    public void Configure(EntityTypeBuilder<IdempotencyRecord> builder)
    {
        builder.ToTable("idempotency_records");
        // PK = unicidade da chave idempotente (escopo global).
        builder.HasKey(i => i.Key);
        builder.Property(i => i.Key).HasMaxLength(200);
        builder.Property(i => i.RequestHash).HasMaxLength(128).IsRequired();
        builder.Property(i => i.Status).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(i => i.ResponseBody).HasColumnType("text");
        builder.Property(i => i.CreatedAtUtc).HasColumnType("timestamp with time zone");

        builder.HasOne(i => i.Loan).WithMany().HasForeignKey(i => i.LoanId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(i => i.LoanId).HasDatabaseName("ix_idempotency_records_loan_id");
    }
}
