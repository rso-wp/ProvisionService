using System.Text.Json;
using Api.Domain;
using Api.Features.PostProvisions;
using Azure.Identity;
using Azure.Storage.Queues;
using Microsoft.Extensions.Options;

namespace Api.Infrastructure.Messaging;

/// <summary>The queues ProvisionFunction reads its commands from.</summary>
public interface IProvisioningQueue
{
    Task ProvisionAsync(ProvisionedService provision, CancellationToken ct);
    Task DeprovisionAsync(DeprovisionTenantSite command, CancellationToken ct);
}

public static class ProvisioningQueueExtensions
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Azure Storage Queues shared with ProvisionFunction: a connection string in <c>ProvisioningQueues</c> (Azurite locally),
    /// or <c>ProvisioningQueues:queueServiceUri</c> with a managed / workload identity.
    /// </summary>
    public static IServiceCollection AddProvisioningQueues(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton(_ =>
        {
            var options = new QueueClientOptions { MessageEncoding = QueueMessageEncoding.None };
            var connectionString = configuration[ProvisioningQueues.Connection];
            if (!string.IsNullOrWhiteSpace(connectionString)) return new QueueServiceClient(connectionString, options);

            var uri = configuration[$"{ProvisioningQueues.Connection}:queueServiceUri"]
                ?? throw new InvalidOperationException(
                    $"'{ProvisioningQueues.Connection}' (connection string) or '{ProvisioningQueues.Connection}:queueServiceUri' is not configured.");
            return new QueueServiceClient(new Uri(uri), new DefaultAzureCredential(), options);
        });
        services.AddSingleton<IProvisioningQueue, StorageProvisioningQueue>();

        return services;
    }

    /// <summary>Creates the three queues if missing (in Azure they are created by terraform; this is a no-op there).</summary>
    public static async Task EnsureProvisioningQueuesAsync(this QueueServiceClient queues, CancellationToken ct)
    {
        foreach (var name in new[] { ProvisioningQueues.Provision, ProvisioningQueues.Deprovision, ProvisioningQueues.Events })
            await queues.GetQueueClient(name).CreateIfNotExistsAsync(cancellationToken: ct);
    }
}

public class StorageProvisioningQueue(QueueServiceClient queues, IOptions<ProvisioningOptions> options) : IProvisioningQueue
{
    public Task ProvisionAsync(ProvisionedService provision, CancellationToken ct) =>
        SendAsync(ProvisioningQueues.Provision, new ProvisionTenantSite(
            provision.Id, provision.Tenant, provision.Name, provision.Host(options.Value.BaseDomain)), ct);

    public Task DeprovisionAsync(DeprovisionTenantSite command, CancellationToken ct) =>
        SendAsync(ProvisioningQueues.Deprovision, command, ct);

    private async Task SendAsync<T>(string queue, T message, CancellationToken ct) =>
        await queues.GetQueueClient(queue)
            .SendMessageAsync(BinaryData.FromObjectAsJson(message, ProvisioningQueueExtensions.Json), cancellationToken: ct);
}
