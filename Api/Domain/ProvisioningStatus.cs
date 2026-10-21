namespace Api.Domain;

public enum ProvisioningStatus
{
    /// <summary>The name is reserved and the subscription is being started; nothing is provisioned yet.</summary>
    PendingPayment,
    Queued,
    Provisioned,
    Failed,
    Deleting
}
