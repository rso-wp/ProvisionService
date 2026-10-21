using System.Text.Json;
using Api.Domain;
using Api.Infrastructure.Messaging;
using Api.Infrastructure.Persistance;
using Microsoft.EntityFrameworkCore;
using NATS.Client.JetStream;
using NATS.Client.JetStream.Models;
using Wolverine.EntityFrameworkCore;

namespace Api.Features.DeleteProvisions;

/// <summary>
/// Durable JetStream consumer for <see cref="SubscriptionEnded"/> (PaymentService: payment failed / cancelled in Stripe).
/// Shuts the site down through the same path as <c>DELETE /provisions/{name}</c>, minus StopSubscription (the subscription
/// is already over): status Deleting + <see cref="DeprovisionService"/> in one transaction.
/// </summary>
public class SubscriptionEndedConsumer(
    INatsJSContext js,
    IServiceScopeFactory scopes,
    ILogger<SubscriptionEndedConsumer> logger) : BackgroundService
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
        await js.EnsurePaymentsStreamAsync(ct);

        var consumer = await js.CreateOrUpdateConsumerAsync(
            PaymentSubjects.Stream,
            new ConsumerConfig(ConsumerName)
            {
                FilterSubject = PaymentSubjects.SubscriptionEnded,
                AckPolicy = ConsumerConfigAckPolicy.Explicit
            },
            ct);

        await foreach (var msg in consumer.ConsumeAsync<byte[]>(cancellationToken: ct))
        {
            try
            {
                var e = JsonSerializer.Deserialize<SubscriptionEnded>(msg.Data ?? [], NatsExtensions.Json)
                    ?? throw new InvalidOperationException("Empty SubscriptionEnded message.");
                await HandleAsync(e, ct);
                await msg.AckAsync(cancellationToken: ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                logger.LogError(ex, "Handling SubscriptionEnded failed, retrying");
                await msg.NakAsync(delay: TimeSpan.FromSeconds(5), cancellationToken: ct);
            }
        }
    }

    private async Task HandleAsync(SubscriptionEnded e, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var outbox = scope.ServiceProvider.GetRequiredService<IDbContextOutbox<ProvisioningDbContext>>();
        var db = outbox.DbContext;

        var provision = await db.ProvisionedServices
            .SingleOrDefaultAsync(p => p.StripeSubscriptionId == e.StripeSubscriptionId, ct);
        if (provision is null || provision.ProvisioningStatus == ProvisioningStatus.Deleting) return;

        logger.LogWarning("Shutting down {Name} ({ProvisionId}): subscription ended ({Reason})",
            provision.Name, provision.Id, e.Reason);

        provision.ProvisioningStatus = ProvisioningStatus.Deleting;
        provision.UpdatedAt = DateTime.UtcNow;
        await outbox.PublishAsync(new DeprovisionService(provision.Id));
        await outbox.SaveChangesAndFlushMessagesAsync(ct);
    }
}
