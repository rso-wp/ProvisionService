using Api.Domain;
using Api.Infrastructure.Messaging;
using Api.Infrastructure.Persistance;
using Azure.Storage.Queues;
using Azure.Storage.Queues.Models;
using Microsoft.EntityFrameworkCore;

namespace Api.Features.ProvisioningEvents;

/// <summary>
/// Reads the events ProvisionFunction sends to <see cref="ProvisioningQueues.Events"/> and updates the provision rows. A
/// message is deleted once handled; on failure it becomes visible again after the visibility timeout and is retried, up to
/// <see cref="ProvisioningQueues.MaxAttempts"/> times.
/// </summary>
public class ProvisioningEventsConsumer(
    QueueServiceClient queues,
    IProvisioningQueue commands,
    IServiceScopeFactory scopes,
    ILogger<ProvisioningEventsConsumer> logger) : BackgroundService
{
    private static readonly TimeSpan VisibilityTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan IdleDelay = TimeSpan.FromSeconds(2);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var queue = queues.GetQueueClient(ProvisioningQueues.Events);
        var ready = false;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (!ready)
                {
                    await queues.EnsureProvisioningQueuesAsync(ct);
                    ready = true;
                    logger.LogInformation("Listening on queue {Queue}", ProvisioningQueues.Events);
                }

                QueueMessage[] messages = await queue.ReceiveMessagesAsync(16, VisibilityTimeout, ct);
                foreach (var message in messages) await HandleMessageAsync(queue, message, ct);
                if (messages.Length == 0) await Task.Delay(IdleDelay, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Reading queue {Queue} failed, retrying in 5s", ProvisioningQueues.Events);
                await Task.Delay(TimeSpan.FromSeconds(5), ct);
            }
        }
    }

    private async Task HandleMessageAsync(QueueClient queue, QueueMessage message, CancellationToken ct)
    {
        try
        {
            var e = message.Body.ToObjectFromJson<ProvisioningEvent>(ProvisioningQueueExtensions.Json)
                ?? throw new InvalidOperationException("Empty provisioning event.");
            await HandleAsync(e, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            if (message.DequeueCount < ProvisioningQueues.MaxAttempts)
            {
                logger.LogError(ex, "Handling event {MessageId} failed (attempt {Attempt}), retrying", message.MessageId, message.DequeueCount);
                return;
            }
            logger.LogError(ex, "Dropping event {MessageId} after {Attempt} attempts: {Body}",
                message.MessageId, message.DequeueCount, message.Body.ToString());
        }

        await queue.DeleteMessageAsync(message.MessageId, message.PopReceipt, ct);
    }

    private async Task HandleAsync(ProvisioningEvent e, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ProvisioningDbContext>();
        var provision = await db.ProvisionedServices.FindAsync([e.ProvisionId], ct);

        switch (e.Type)
        {
            case ProvisioningEventTypes.Provisioned:
                if (provision is null || provision.ProvisioningStatus == ProvisioningStatus.Deleting)
                {
                    await RemoveLateSiteAsync(db, e, provision, ct);
                    return;
                }
                provision.ProvisioningStatus = ProvisioningStatus.Provisioned;
                provision.Url = e.Url;
                break;

            case ProvisioningEventTypes.ProvisioningFailed:
                if (provision is null || provision.ProvisioningStatus == ProvisioningStatus.Deleting) return;

                logger.LogWarning("Provision {ProvisionId} failed: {Reason}", e.ProvisionId, e.Reason);
                provision.ProvisioningStatus = ProvisioningStatus.Failed;
                break;

            case ProvisioningEventTypes.Destroyed:
                if (provision is not null) db.ProvisionedServices.Remove(provision);
                break;

            default:
                logger.LogWarning("Ignoring provisioning event of unknown type {Type}", e.Type);
                return;
        }

        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// The queues do not keep commands in order, so a site can finish starting after it was deleted (its row is Deleting,
    /// or already gone). Remove it again, unless a new site with the same name has been created since.
    /// </summary>
    private async Task RemoveLateSiteAsync(ProvisioningDbContext db, ProvisioningEvent e, ProvisionedService? provision, CancellationToken ct)
    {
        if (provision is null && await db.ProvisionedServices.AnyAsync(p => p.Tenant == e.Tenant && p.Name == e.Name, ct))
            return;

        logger.LogInformation("Site {Name}.{Tenant} came up after it was deleted, removing it again", e.Name, e.Tenant);
        await commands.DeprovisionAsync(new DeprovisionTenantSite(e.ProvisionId, e.Tenant, e.Name), ct);
    }
}
