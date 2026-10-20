using Microsoft.Extensions.DependencyInjection;

namespace LibraryLoans.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services)
    {
        // DbContext (PostgreSQL), Redis e repositórios serão registrados aqui.
        return services;
    }
}
