using Api.Domain;

namespace Api.Infrastructure.CloudFunction;

public interface ICloudFunctionTrigger
{
    Task TriggerProvisioningAsync(ProvisionedService provision, CancellationToken ct);
}

public class CloudFunctionTrigger(ILogger<CloudFunctionTrigger> logger) : ICloudFunctionTrigger
{
    public Task TriggerProvisioningAsync(ProvisionedService provision, CancellationToken ct)
    {
        // TODO: place a message on the queue that triggers the cloud function which starts the docker image
        // (provision.ServiceName : provision.ServiceVersion). The function answers with a ServiceProvisioned
        // message, which updates the provision status.
        logger.LogInformation(
            "TODO: trigger cloud function for provision {ProvisionId} ({Service}:{Version})",
            provision.Id, provision.ServiceName, provision.ServiceVersion);

        return Task.CompletedTask;
    }
}
