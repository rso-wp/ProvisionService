using Api.Infrastructure.CloudFunction;
using Api.Infrastructure.Persistance;

namespace Api.Features.PostProvisions;

public static class ProvisionServiceHandler
{
    /// <summary>Picked up by Wolverine from the (durable) queue after the provision row was committed.</summary>
    public static async Task Handle(
        ProvisionService message,
        ProvisioningDbContext db,
        ICloudFunctionTrigger cloudFunction,
        ILogger<ProvisionService> logger,
        CancellationToken ct)
    {
        var provision = await db.ProvisionedServices.FindAsync([message.ProvisionId], ct);
        if (provision is null)
        {
            logger.LogWarning("Provision {ProvisionId} not found, dropping message.", message.ProvisionId);
            return;
        }

        await cloudFunction.TriggerProvisioningAsync(provision, ct);
    }
}
