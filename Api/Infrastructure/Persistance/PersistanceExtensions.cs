using Microsoft.EntityFrameworkCore;
using Wolverine;
using Wolverine.EntityFrameworkCore;
using Wolverine.Postgresql;

namespace Api.Infrastructure.Persistance;

public static class PersistanceExtensions
{
    public static string GetDefaultConnectionString(this IConfiguration configuration) =>
        configuration.GetConnectionString("DefaultConnection")
            ?? throw new InvalidOperationException("Connection string 'DefaultConnection' is not configured.");

    public static IServiceCollection AddProvisioningPersistence(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetDefaultConnectionString();

        // Wolverine registers the DbContext itself so its transactional outbox can enlist in the same transaction.
        services.AddDbContextWithWolverineIntegration<ProvisioningDbContext>(options =>
            options.UseNpgsql(connectionString));

        return services;
    }

    public static void AddProvisioningMessaging(this WolverineOptions options, IConfiguration configuration)
    {
        options.PersistMessagesWithPostgresql(configuration.GetDefaultConnectionString(), ProvisioningDbContext.Schema);
        options.UseEntityFrameworkCoreTransactions();
        options.Policies.UseDurableLocalQueues();
    }
}
