using Api.Domain;
using Api.Infrastructure.Persistance;
using Api.Protos;
using Microsoft.EntityFrameworkCore;
using Wolverine.EntityFrameworkCore;

namespace Api.Features.PostProvisions;

/// <summary>PaymentService's answer for a <see cref="ProvisioningStatus.PendingPayment"/> site, and what it does to the row.</summary>
public static class PendingPayments
{
    public const string Successful = "SUCCESSFUL";
    public const string Failed = "FAILED";

    /// <summary>
    /// How long to wait for PaymentService. A slower answer is not lost: the site stays PendingPayment and
    /// ProvisioningReconciler asks for it (ResolveSubscription).
    /// </summary>
    public static DateTime Deadline() => DateTime.UtcNow.AddSeconds(30);

    /// <summary>
    /// Started: the site becomes Queued with its subscription, and the <see cref="ProvisionService"/> command is stored in the
    /// same transaction (outbox). Declined: the row is removed, which frees the name (nothing was charged). Anything else
    /// ("PENDING") leaves it as it is.
    /// </summary>
    public static async Task ApplyAsync(
        IDbContextOutbox<ProvisioningDbContext> outbox, ProvisionedService provision, TryMakeSubscriptionResponse answer)
    {
        switch (answer.Status)
        {
            case Successful:
                provision.StripeSubscriptionId = answer.StripeSubscriptionId;
                provision.ProvisioningStatus = ProvisioningStatus.Queued;
                provision.UpdatedAt = DateTime.UtcNow;
                await outbox.PublishAsync(new ProvisionService(provision.Id));
                await outbox.SaveChangesAndFlushMessagesAsync(CancellationToken.None);
                break;

            case Failed:
                outbox.DbContext.ProvisionedServices.Remove(provision);
                try
                {
                    await outbox.DbContext.SaveChangesAsync(CancellationToken.None);
                }
                catch (DbUpdateConcurrencyException)
                {
                    // Already removed by a concurrent resolution of the same answer.
                }
                break;
        }
    }
}
