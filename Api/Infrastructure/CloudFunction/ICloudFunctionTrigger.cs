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
        // TODO: place a message on the queue that triggers the cloud function which installs the WordPress
        // helm chart (dev/rso/charts/wordpress) as release "wp-{provision.Name}" with tenant.name = provision.Name
        // and the tenant's separately provisioned database. The function answers with a ServiceProvisioned
        // message, which updates the provision status.
        logger.LogInformation(
            "TODO: trigger cloud function for provision {ProvisionId} (tenant {Tenant})",
            provision.Id, provision.Name);

        return Task.CompletedTask;
    }
}
