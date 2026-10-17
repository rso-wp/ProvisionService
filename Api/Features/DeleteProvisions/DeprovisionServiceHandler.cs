using Api.Infrastructure.Messaging;
using Api.Infrastructure.Persistance;

namespace Api.Features.DeleteProvisions;

public static class DeprovisionServiceHandler
{
    /// <summary>Forwards the delete to ProvisionFunction over NATS once the Deleting status was committed.</summary>
    public static async Task Handle(
        DeprovisionService message,
        ProvisioningDbContext db,
        IProvisioningQueue queue,
        ILogger<DeprovisionService> logger,
        CancellationToken ct)
    {
        var provision = await db.ProvisionedServices.FindAsync([message.ProvisionId], ct);
        if (provision is null)
        {
            logger.LogWarning("Provision {ProvisionId} not found, dropping message.", message.ProvisionId);
            return;
        }

        await queue.DeprovisionAsync(provision, ct);
    }
}
