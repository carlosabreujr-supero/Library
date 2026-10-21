using LibraryLoans.Application.Abstractions;
using LibraryLoans.Application.Loans;
using LibraryLoans.Infrastructure.Caching;
using LibraryLoans.Infrastructure.Health;
using LibraryLoans.Infrastructure.Idempotency;
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

        services.AddDbContext<LibraryDbContext>(options => options.UseNpgsql(connectionString, npgsql => npgsql.EnableRetryOnFailure(maxRetryCount: 3, maxRetryDelay: TimeSpan.FromSeconds(2), errorCodesToAdd: null)));

        services.AddScoped<IBookRepository, BookRepository>();
        services.AddScoped<IReaderRepository, ReaderRepository>();
        services.AddScoped<ILoanRepository, LoanRepository>();
        services.AddScoped<IIdempotencyRepository, IdempotencyRepository>();

        var idempotency = configuration.GetSection("Idempotency").Get<IdempotencySettings>() ?? new();
        idempotency.Validate();
        services.AddSingleton(idempotency);
        services.AddHostedService<IdempotencyPurgeService>();

        services.AddScoped<IAuditQueries, AuditQueries>();
        services.AddScoped<ILoanQueries, LoanQueries>();
        services.AddScoped<IAuditTrail, AuditTrail>();
        services.AddScoped<IUnitOfWork, UnitOfWork>();

        var cacheSettings = configuration.GetSection("Cache").Get<CacheSettings>() ?? new();
        cacheSettings.Validate();
        services.AddSingleton(cacheSettings);
        if (cacheSettings.Enabled)
        {
            var redisConnection = configuration.GetConnectionString("Redis")
                ?? throw new InvalidOperationException("ConnectionStrings:Redis não configurada (ou defina Cache:Enabled=false).");

            // O cache é opcional: a API deve subir e operar mesmo com o Redis fora do ar.
            var redisOptions = StackExchange.Redis.ConfigurationOptions.Parse(redisConnection);
            redisOptions.AbortOnConnectFail = false;
            redisOptions.ConnectTimeout = cacheSettings.TimeoutMs;
            redisOptions.SyncTimeout = cacheSettings.TimeoutMs;
            redisOptions.AsyncTimeout = cacheSettings.TimeoutMs;
            services.AddStackExchangeRedisCache(o => o.ConfigurationOptions = redisOptions);
            services.AddSingleton<IReadCache, RedisReadCache>();
        }
        else
        {
            services.AddSingleton<IReadCache, NoReadCache>();
        }

        var healthSettings = configuration.GetSection("Health").Get<HealthSettings>() ?? new();
        services.AddSingleton(healthSettings);

        return services;
    }
}
