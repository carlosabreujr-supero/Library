using Microsoft.EntityFrameworkCore;

namespace LibraryLoans.Infrastructure.Persistence;

public class LibraryDbContext(DbContextOptions<LibraryDbContext> options) : DbContext(options)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // Descobre automaticamente todas as IEntityTypeConfiguration<T> do assembly.
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(LibraryDbContext).Assembly);
    }
}
