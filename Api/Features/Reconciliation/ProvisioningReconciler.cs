using Api.Domain;
using Api.Features.DeleteProvisions;
using Api.Features.PostProvisions;
using Api.Infrastructure.Persistance;
using Api.Protos;
using Grpc.Core;
using Microsoft.EntityFrameworkCore;
using Wolverine;
using Wolverine.EntityFrameworkCore;

namespace Api.Features.Reconciliation;

/// <summary>
/// Safety net that brings every site to its end state, whatever was lost on the way (a crash, a client that went away, a
/// command given up on by ProvisionFunction, a message that never arrived). Every minute:
/// <list type="bullet">
/// <item>PendingPayment for over <see cref="PaymentStaleAfter"/>: ask PaymentService what became of the payment
/// (ResolveSubscription) and continue, or release the name;</item>
/// <item>Queued, Failed or Deleting with no progress for <see cref="CommandStaleAfter"/>: send the command again.</item>
/// </list>
/// Commands are idempotent, so one that was only slow runs twice. On several replicas each row is claimed first.
/// </summary>
public class ProvisioningReconciler(IServiceScopeFactory scopes, ILogger<ProvisioningReconciler> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan PaymentStaleAfter = TimeSpan.FromMinutes(2);

    // Longer than ProvisionFunction's own retries of one command (about 45 minutes), so a command is sent again only once
    // those are over.
    private static readonly TimeSpan CommandStaleAfter = TimeSpan.FromHours(1);

    private const int BatchSize = 50;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        try
        {
            using var timer = new PeriodicTimer(Interval);
            while (await timer.WaitForNextTickAsync(ct))
            {
                try
                {
                    await ResolvePaymentsAsync(ct);
                    await ResendCommandsAsync(ct);
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    logger.LogError(ex, "Reconciling provisions failed, trying again in {Interval}", Interval);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
    }

    private async Task ResolvePaymentsAsync(CancellationToken ct)
    {
        List<Guid> pending;
        await using (var scope = scopes.CreateAsyncScope())
        {
            var staleBefore = DateTime.UtcNow - PaymentStaleAfter;
            pending = await scope.ServiceProvider.GetRequiredService<ProvisioningDbContext>().ProvisionedServices
                .Where(p => p.ProvisioningStatus == ProvisioningStatus.PendingPayment && p.UpdatedAt < staleBefore)
                .OrderBy(p => p.UpdatedAt)
                .Select(p => p.Id)
                .Take(BatchSize)
                .ToListAsync(ct);
        }

        foreach (var id in pending)
        {
            await using var scope = scopes.CreateAsyncScope();
            var outbox = scope.ServiceProvider.GetRequiredService<IDbContextOutbox<ProvisioningDbContext>>();
            var payments = scope.ServiceProvider.GetRequiredService<Payment.PaymentClient>();

            var provision = await outbox.DbContext.ProvisionedServices.FindAsync([id], ct);
            if (provision is not { ProvisioningStatus: ProvisioningStatus.PendingPayment }) continue;

            TryMakeSubscriptionResponse answer;
            try
            {
                answer = await payments.ResolveSubscriptionAsync(
                    new ResolveSubscriptionRequest { RequestId = id.ToString() },
                    deadline: PendingPayments.Deadline(),
                    cancellationToken: ct);
            }
            catch (RpcException ex)
            {
                logger.LogWarning(ex, "Could not resolve the payment of provision {ProvisionId}; trying again later", id);
                return; // PaymentService is unavailable: the other rows would fail the same way
            }

            logger.LogInformation("Payment of {Name}.{Tenant} ({ProvisionId}) resolved: {Status} {Reason}",
                provision.Name, provision.Tenant, id, answer.Status, answer.Reason);
            await PendingPayments.ApplyAsync(outbox, provision, answer);
        }
    }

    private async Task ResendCommandsAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ProvisioningDbContext>();
        var bus = scope.ServiceProvider.GetRequiredService<IMessageBus>();

        var now = DateTime.UtcNow;
        var staleBefore = now - CommandStaleAfter;
        var stale = await db.ProvisionedServices
            .Where(p => (p.ProvisioningStatus == ProvisioningStatus.Queued
                         || p.ProvisioningStatus == ProvisioningStatus.Failed
                         || p.ProvisioningStatus == ProvisioningStatus.Deleting)
                        && p.UpdatedAt < staleBefore)
            .OrderBy(p => p.UpdatedAt)
            .Select(p => new { p.Id, p.ProvisioningStatus, p.UpdatedAt })
            .Take(BatchSize)
            .ToListAsync(ct);

        foreach (var site in stale)
        {
            // Claim the row (move UpdatedAt) so another replica does not send the command as well.
            var claimed = await db.ProvisionedServices
                .Where(p => p.Id == site.Id && p.ProvisioningStatus == site.ProvisioningStatus && p.UpdatedAt == site.UpdatedAt)
                .ExecuteUpdateAsync(s => s.SetProperty(p => p.UpdatedAt, now), ct);
            if (claimed == 0) continue;

            logger.LogWarning("Provision {ProvisionId} has been {Status} since {Since:u}; sending its command again",
                site.Id, site.ProvisioningStatus, site.UpdatedAt);
            if (site.ProvisioningStatus == ProvisioningStatus.Deleting)
                await bus.PublishAsync(new DeprovisionService(site.Id));
            else
                await bus.PublishAsync(new ProvisionService(site.Id));
        }
    }
}
