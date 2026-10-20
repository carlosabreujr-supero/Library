using LibraryLoans.Application.Abstractions;
using LibraryLoans.Infrastructure.Persistence;
using LibraryLoans.Infrastructure.Persistence.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace LibraryLoans.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("Postgres")
            ?? throw new InvalidOperationException("ConnectionStrings:Postgres não configurada.");

        services.AddDbContext<LibraryDbContext>(options => options.UseNpgsql(connectionString));

        services.AddScoped<IBookRepository, BookRepository>();
        services.AddScoped<IReaderRepository, ReaderRepository>();
        services.AddScoped<ILoanQueries, LoanQueries>();
        services.AddScoped<IAuditTrail, AuditTrail>();
        services.AddScoped<IUnitOfWork, UnitOfWork>();

        // Redis e repositórios serão registrados aqui.
        return services;
    }
}
