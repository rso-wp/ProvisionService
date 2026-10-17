using Api.Infrastructure.Messaging;
using Api.Infrastructure.Persistance;

namespace Api.Features.PostProvisions;

public static class ProvisionServiceHandler
{
    /// <summary>
    /// Picked up by Wolverine from the (durable) local queue after the provision row was committed, and forwarded to
    /// ProvisionFunction over NATS. Failures are retried by Wolverine (see AddProvisioningMessaging).
    /// </summary>
    public static async Task Handle(
        ProvisionService message,
        ProvisioningDbContext db,
        IProvisioningQueue queue,
        ILogger<ProvisionService> logger,
        CancellationToken ct)
    {
        var provision = await db.ProvisionedServices.FindAsync([message.ProvisionId], ct);
        if (provision is null)
        {
            logger.LogWarning("Provision {ProvisionId} not found, dropping message.", message.ProvisionId);
            return;
        }

        await queue.ProvisionAsync(provision, ct);
    }
}
