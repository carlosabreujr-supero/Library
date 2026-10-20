using LibraryLoans.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace LibraryLoans.Infrastructure.Persistence.Configurations;

public class BookConfiguration : IEntityTypeConfiguration<Book>
{
    public void Configure(EntityTypeBuilder<Book> builder)
    {
        builder.ToTable("books", t =>
        {
            t.HasCheckConstraint("ck_books_total_copies_non_negative", "\"TotalCopies\" >= 0");
            t.HasCheckConstraint("ck_books_available_copies_range", "\"AvailableCopies\" >= 0 AND \"AvailableCopies\" <= \"TotalCopies\"");
        });

        builder.HasKey(b => b.Id);
        builder.Property(b => b.Id).ValueGeneratedNever();
        builder.Property(b => b.Title).HasMaxLength(300).IsRequired();
        builder.Property(b => b.Isbn).HasColumnName("ISBN").HasMaxLength(20).IsRequired();
        builder.Property(b => b.Author).HasMaxLength(200).IsRequired();
        builder.Property(b => b.CreatedAtUtc).HasColumnType("timestamp with time zone");
        builder.Property(b => b.UpdatedAtUtc).HasColumnType("timestamp with time zone");

        builder.HasIndex(b => b.Isbn).IsUnique().HasDatabaseName("ux_books_isbn");
    }
}
