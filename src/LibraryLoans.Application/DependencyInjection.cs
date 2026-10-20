using LibraryLoans.Application.Books;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace LibraryLoans.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);

        services.AddScoped<CreateBookHandler>();
        services.AddScoped<UpdateBookHandler>();
        services.AddScoped<DeactivateBookHandler>();
        services.AddScoped<GetBookHandler>();
        services.AddScoped<ListBooksHandler>();

        return services;
    }
}
