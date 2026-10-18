namespace Api.Domain;

/// <summary>A WordPress site, served at <c>Name.Tenant.baseDomain</c>.</summary>
public class ProvisionedService
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    /// <summary>The owner's tenant slug (from UserService), copied so the host never changes.</summary>
    public required string Tenant { get; set; }
    /// <summary>Site name: a DNS label, unique within the tenant.</summary>
    public required string Name { get; set; }
    public required string StripeSubscriptionId { get; set; }
    public ProvisioningStatus ProvisioningStatus { get; set; }
    /// <summary>Where the site is reachable; set when ProvisionFunction reports it running.</summary>
    public string? Url { get; set; }
    public DateTime StartDate { get; set; }

    public string Host(string baseDomain) => $"{Name}.{Tenant}.{baseDomain}";
}
