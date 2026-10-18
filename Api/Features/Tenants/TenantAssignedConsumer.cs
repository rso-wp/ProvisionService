using System.Text.Json;
using Api.Domain;
using Api.Infrastructure.Messaging;
using Api.Infrastructure.Persistance;
using NATS.Client.JetStream;
using NATS.Client.JetStream.Models;

namespace Api.Features.Tenants;

/// <summary>
/// Durable JetStream consumer for <see cref="TenantAssigned"/> (UserService): keeps a local copy of each user's tenant slug,
/// needed to build site host names. Idempotent; a tenant never changes once assigned.
/// </summary>
public class TenantAssignedConsumer(
    INatsJSContext js,
    IServiceScopeFactory scopes,
    ILogger<TenantAssignedConsumer> logger) : BackgroundService
{
    private const string ConsumerName = "provisioning-service";

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await ConsumeAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "NATS consumer stopped, reconnecting in 5s");
                await Task.Delay(TimeSpan.FromSeconds(5), ct);
            }
        }
    }

    private async Task ConsumeAsync(CancellationToken ct)
    {
        await js.EnsureUsersStreamAsync(ct);

        var consumer = await js.CreateOrUpdateConsumerAsync(
            UserSubjects.Stream,
            new ConsumerConfig(ConsumerName)
            {
                FilterSubject = UserSubjects.TenantAssigned,
                AckPolicy = ConsumerConfigAckPolicy.Explicit
            },
            ct);

        await foreach (var msg in consumer.ConsumeAsync<byte[]>(cancellationToken: ct))
        {
            try
            {
                var e = JsonSerializer.Deserialize<TenantAssigned>(msg.Data ?? [], NatsExtensions.Json)
                    ?? throw new InvalidOperationException("Empty TenantAssigned message.");

                using var scope = scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<ProvisioningDbContext>();
                if (await db.UserTenants.FindAsync([e.UserId], ct) is null)
                {
                    db.UserTenants.Add(new UserTenant { UserId = e.UserId, Tenant = e.Tenant });
                    await db.SaveChangesAsync(ct);
                    logger.LogInformation("User {UserId} has tenant {Tenant}", e.UserId, e.Tenant);
                }

                await msg.AckAsync(cancellationToken: ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                logger.LogError(ex, "Handling TenantAssigned failed, retrying");
                await msg.NakAsync(delay: TimeSpan.FromSeconds(5), cancellationToken: ct);
            }
        }
    }
}
