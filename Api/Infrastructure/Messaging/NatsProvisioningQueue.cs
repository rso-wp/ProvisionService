using Api.Domain;
using NATS.Client.JetStream;

namespace Api.Infrastructure.Messaging;

/// <summary>The queue ProvisionFunction reads its commands from.</summary>
public interface IProvisioningQueue
{
    Task ProvisionAsync(ProvisionedService provision, CancellationToken ct);
    Task DeprovisionAsync(ProvisionedService provision, CancellationToken ct);
}

public class NatsProvisioningQueue(INatsJSContext js) : IProvisioningQueue
{
    public async Task ProvisionAsync(ProvisionedService provision, CancellationToken ct)
    {
        await js.EnsureProvisioningStreamAsync(ct);
        await js.PublishJsonAsync(ProvisioningSubjects.Provision, new ProvisionTenantSite(provision.Id, provision.Name), ct);
    }

    public async Task DeprovisionAsync(ProvisionedService provision, CancellationToken ct)
    {
        await js.EnsureProvisioningStreamAsync(ct);
        await js.PublishJsonAsync(ProvisioningSubjects.Deprovision, new DeprovisionTenantSite(provision.Id, provision.Name), ct);
    }
}
