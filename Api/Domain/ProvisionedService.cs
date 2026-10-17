namespace Api.Domain;

public class ProvisionedService
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public required string ServiceId { get; set; }
    public required string ServiceName { get; set; }
    public required string ServiceVersion { get; set; }
    public required string StripeSubscriptionId { get; set; }
    public ProvisioningStatus ProvisioningStatus { get; set; }
    public DateTime StartDate { get; set; }
}
