using Api.Domain;
using Api.Features.PostProvisions;
using Microsoft.Extensions.Options;
using NATS.Client.JetStream;

namespace Api.Infrastructure.Messaging;

/// <summary>The queue ProvisionFunction reads its commands from.</summary>
public interface IProvisioningQueue
{
    Task ProvisionAsync(ProvisionedService provision, CancellationToken ct);
    Task DeprovisionAsync(ProvisionedService provision, CancellationToken ct);
}

public class NatsProvisioningQueue(INatsJSContext js, IOptions<ProvisioningOptions> options) : IProvisioningQueue
{
    public async Task ProvisionAsync(ProvisionedService provision, CancellationToken ct)
    {
        await js.EnsureProvisioningStreamAsync(ct);
        await js.PublishJsonAsync(ProvisioningSubjects.Provision, new ProvisionTenantSite(
            provision.Id, provision.Tenant, provision.Name, provision.Host(options.Value.BaseDomain)), ct);
    }

    public async Task DeprovisionAsync(ProvisionedService provision, CancellationToken ct)
    {
        await js.EnsureProvisioningStreamAsync(ct);
        await js.PublishJsonAsync(ProvisioningSubjects.Deprovision, new DeprovisionTenantSite(provision.Id, provision.Tenant, provision.Name), ct);
    }
}
