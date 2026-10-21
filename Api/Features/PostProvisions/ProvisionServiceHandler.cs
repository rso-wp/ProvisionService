using Api.Domain;
using Api.Infrastructure.Messaging;
using Api.Infrastructure.Persistance;

namespace Api.Features.PostProvisions;

public static class ProvisionServiceHandler
{
    /// <summary>
    /// Picked up by Wolverine from the (durable) local queue after the provision row was committed, and forwarded to
    /// ProvisionFunction (storage queue). Failures are retried by Wolverine (see AddProvisioningMessaging).
    /// </summary>
    public static async Task Handle(
        ProvisionService message,
        ProvisioningDbContext db,
        IProvisioningQueue queue,
        ILogger<ProvisionService> logger,
        CancellationToken ct)
    {
        var provision = await db.ProvisionedServices.FindAsync([message.ProvisionId], ct);
        // Only a site that should be running: not one deleted (or being deleted) while this message waited.
        if (provision is not { ProvisioningStatus: ProvisioningStatus.Queued or ProvisioningStatus.Failed })
        {
            logger.LogInformation("Provision {ProvisionId} is {Status}, not provisioning it", message.ProvisionId,
                provision?.ProvisioningStatus.ToString() ?? "gone");
            return;
        }

        await queue.ProvisionAsync(provision, ct);
    }
}
