using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LibraryLoans.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BookConcurrencyToken : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // xmin é coluna de sistema do PostgreSQL; nenhuma alteração de schema é necessária.
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
        }
    }
}
