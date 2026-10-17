namespace Api.Domain;

/// <summary>A tenant's WordPress site.</summary>
public class ProvisionedService
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    /// <summary>Tenant name: a DNS label, unique. Used for the helm release and the site host name.</summary>
    public required string Name { get; set; }
    public required string StripeSubscriptionId { get; set; }
    public ProvisioningStatus ProvisioningStatus { get; set; }
    /// <summary>Where the site is reachable; set when ProvisionFunction reports it running.</summary>
    public string? Url { get; set; }
    public DateTime StartDate { get; set; }
}
