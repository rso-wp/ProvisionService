using System.Text.Json;
using Api.Domain;
using Api.Infrastructure.Messaging;
using Api.Infrastructure.Persistance;
using Microsoft.EntityFrameworkCore;
using NATS.Client.JetStream;
using NATS.Client.JetStream.Models;

namespace Api.Features.ProvisioningEvents;

/// <summary>
/// Durable JetStream consumer for the events ProvisionFunction publishes. Updates the provision row and acks;
/// on failure the event is redelivered.
/// </summary>
public class ProvisioningEventsConsumer(
    INatsJSContext js,
    IServiceScopeFactory scopes,
    ILogger<ProvisioningEventsConsumer> logger) : BackgroundService
{
    private const string ConsumerName = "provisioning-service";

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await ConsumeAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "NATS consumer stopped, reconnecting in 5s");
                await Task.Delay(TimeSpan.FromSeconds(5), ct);
            }
        }
    }

    private async Task ConsumeAsync(CancellationToken ct)
    {
        await js.EnsureProvisioningStreamAsync(ct);

        var consumer = await js.CreateOrUpdateConsumerAsync(
            ProvisioningSubjects.Stream,
            new ConsumerConfig(ConsumerName)
            {
                FilterSubject = ProvisioningSubjects.Events,
                AckPolicy = ConsumerConfigAckPolicy.Explicit
            },
            ct);

        await foreach (var msg in consumer.ConsumeAsync<byte[]>(cancellationToken: ct))
        {
            try
            {
                await HandleAsync(msg.Subject, msg.Data ?? [], ct);
                await msg.AckAsync(cancellationToken: ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                logger.LogError(ex, "Handling {Subject} failed, retrying", msg.Subject);
                await msg.NakAsync(delay: TimeSpan.FromSeconds(5), cancellationToken: ct);
            }
        }
    }

    private async Task HandleAsync(string subject, byte[] data, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ProvisioningDbContext>();

        switch (subject)
        {
            case ProvisioningSubjects.Provisioned:
            {
                var e = Deserialize<TenantSiteProvisioned>(data);
                var provision = await db.ProvisionedServices.FindAsync([e.ProvisionId], ct);
                if (provision is null || provision.ProvisioningStatus == ProvisioningStatus.Deleting) return;

                provision.ProvisioningStatus = ProvisioningStatus.Provisioned;
                provision.Url = e.Url;
                break;
            }
            case ProvisioningSubjects.ProvisioningFailed:
            {
                var e = Deserialize<TenantSiteProvisioningFailed>(data);
                var provision = await db.ProvisionedServices.FindAsync([e.ProvisionId], ct);
                if (provision is null || provision.ProvisioningStatus == ProvisioningStatus.Deleting) return;

                logger.LogWarning("Provision {ProvisionId} failed: {Reason}", e.ProvisionId, e.Reason);
                provision.ProvisioningStatus = ProvisioningStatus.Failed;
                break;
            }
            case ProvisioningSubjects.Destroyed:
            {
                var e = Deserialize<TenantSiteDestroyed>(data);
                await db.ProvisionedServices.Where(p => p.Id == e.ProvisionId).ExecuteDeleteAsync(ct);
                return;
            }
            default:
                logger.LogWarning("Ignoring event on unknown subject {Subject}", subject);
                return;
        }

        await db.SaveChangesAsync(ct);
    }

    private static T Deserialize<T>(byte[] data) =>
        JsonSerializer.Deserialize<T>(data, NatsExtensions.Json)
        ?? throw new InvalidOperationException($"Empty {typeof(T).Name} event.");
}
