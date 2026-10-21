using System.Text.Json;
using Api.Domain;
using Api.Infrastructure.Messaging;
using Api.Infrastructure.Persistance;
using Azure.Storage.Queues;
using Azure.Storage.Queues.Models;
using Microsoft.EntityFrameworkCore;

namespace Api.Features.ProvisioningEvents;

/// <summary>
/// Reads the events ProvisionFunction sends to <see cref="ProvisioningQueues.Events"/> and updates the provision rows. A
/// message is deleted once handled. A failure (almost always the database being unavailable) never drops it: it becomes
/// visible again after a delay that grows with each attempt, up to <see cref="MaxRetryDelay"/>. Only a message that cannot
/// be read at all is moved to <see cref="ProvisioningQueues.EventsPoison"/>.
/// </summary>
public class ProvisioningEventsConsumer(
    QueueServiceClient queues,
    IProvisioningQueue commands,
    IServiceScopeFactory scopes,
    ILogger<ProvisioningEventsConsumer> logger) : BackgroundService
{
    private static readonly TimeSpan VisibilityTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan MaxRetryDelay = TimeSpan.FromMinutes(10);
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
        if (Read(message) is not { } e)
        {
            logger.LogError("Moving unreadable event {MessageId} to {Queue}: {Body}",
                message.MessageId, ProvisioningQueues.EventsPoison, message.Body.ToString());
            await queues.GetQueueClient(ProvisioningQueues.EventsPoison)
                .SendMessageAsync(message.Body, timeToLive: ProvisioningQueues.Forever, cancellationToken: ct);
            await queue.DeleteMessageAsync(message.MessageId, message.PopReceipt, ct);
            return;
        }

        try
        {
            await HandleAsync(e, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            var delay = RetryDelay(message.DequeueCount);
            logger.LogError(ex, "Handling event {MessageId} ({Type} {ProvisionId}) failed (attempt {Attempt}), retrying in {Delay}",
                message.MessageId, e.Type, e.ProvisionId, message.DequeueCount, delay);
            await queue.UpdateMessageAsync(message.MessageId, message.PopReceipt, visibilityTimeout: delay, cancellationToken: ct);
            return;
        }

        await queue.DeleteMessageAsync(message.MessageId, message.PopReceipt, ct);
    }

    private static ProvisioningEvent? Read(QueueMessage message)
    {
        try
        {
            return message.Body.ToObjectFromJson<ProvisioningEvent>(ProvisioningQueueExtensions.Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>30 s after the first failure, doubling up to <see cref="MaxRetryDelay"/>.</summary>
    private static TimeSpan RetryDelay(long attempt) =>
        TimeSpan.FromTicks(Math.Min(MaxRetryDelay.Ticks, VisibilityTimeout.Ticks << (int)Math.Clamp(attempt - 1, 0, 10)));

    /// <summary>
    /// Status changes are conditional updates, so an event never overwrites a delete committed in the meantime (the event
    /// may have been read before it).
    /// </summary>
    private async Task HandleAsync(ProvisioningEvent e, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ProvisioningDbContext>();
        var site = db.ProvisionedServices.Where(p => p.Id == e.ProvisionId);
        var now = DateTime.UtcNow;

        switch (e.Type)
        {
            case ProvisioningEventTypes.Provisioned:
                // Provisioned too: a repeated event only sets the same values again.
                var running = await site
                    .Where(p => p.ProvisioningStatus == ProvisioningStatus.Queued
                                || p.ProvisioningStatus == ProvisioningStatus.Failed
                                || p.ProvisioningStatus == ProvisioningStatus.Provisioned)
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(p => p.ProvisioningStatus, ProvisioningStatus.Provisioned)
                        .SetProperty(p => p.Url, e.Url)
                        .SetProperty(p => p.UpdatedAt, now), ct);
                if (running == 0) await RemoveLateSiteAsync(e, "came up after it was deleted", ct);
                break;

            case ProvisioningEventTypes.ProvisioningFailed:
                var failed = await site
                    .Where(p => p.ProvisioningStatus == ProvisioningStatus.Queued || p.ProvisioningStatus == ProvisioningStatus.Failed)
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(p => p.ProvisioningStatus, ProvisioningStatus.Failed)
                        .SetProperty(p => p.UpdatedAt, now), ct);
                if (failed == 1)
                {
                    logger.LogWarning("Provision {ProvisionId} failed: {Reason}", e.ProvisionId, e.Reason);
                    break;
                }
                // Running already: an older attempt failed after a later one succeeded. Otherwise the site is being
                // deleted or gone, and the failed attempt may have left parts of it behind.
                if (!await site.AnyAsync(p => p.ProvisioningStatus == ProvisioningStatus.Provisioned, ct))
                    await RemoveLateSiteAsync(e, "failed after it was deleted", ct);
                break;

            case ProvisioningEventTypes.Destroyed:
                await site.ExecuteDeleteAsync(ct);
                break;

            default:
                logger.LogWarning("Ignoring provisioning event of unknown type {Type}", e.Type);
                break;
        }
    }

    /// <summary>
    /// The queues do not keep commands in order, so a site can finish starting (or fail half-way) after it was deleted: its
    /// row is Deleting, or already gone. Everything created for a site is named after its provision id, so removing it
    /// again can never touch a newer site with the same name.
    /// </summary>
    private async Task RemoveLateSiteAsync(ProvisioningEvent e, string what, CancellationToken ct)
    {
        logger.LogInformation("Site {Name}.{Tenant} ({ProvisionId}) {What}, removing it", e.Name, e.Tenant, e.ProvisionId, what);
        await commands.DeprovisionAsync(new DeprovisionTenantSite(e.ProvisionId, e.Tenant, e.Name), ct);
    }
}
