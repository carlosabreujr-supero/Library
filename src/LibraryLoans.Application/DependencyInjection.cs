using Microsoft.Extensions.DependencyInjection;

namespace LibraryLoans.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        // Command/Query handlers serão registrados aqui.
        return services;
    }
}
