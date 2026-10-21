using System.Security.Claims;
using Api.Domain;
using Api.Features.PostProvisions;
using Api.Infrastructure.Auth;
using Api.Infrastructure.Persistance;
using Api.Protos;
using Grpc.Core;
using Microsoft.EntityFrameworkCore;
using Wolverine.EntityFrameworkCore;

namespace Api.Features.DeleteProvisions;

public static class DeleteProvisionsEndpoint
{
    private const string SubscriptionStopped = "SUCCESSFUL";

    /// <summary>
    /// DELETE /provisions/{name}. Stops the subscription in PaymentService, then marks the site Deleting and stores a
    /// <see cref="DeprovisionService"/> message in one transaction (Wolverine outbox). The row is removed when
    /// ProvisionFunction reports that the container and database are gone.
    /// </summary>
    public static IEndpointRouteBuilder MapDeleteProvisions(this IEndpointRouteBuilder app)
    {
        app.MapDelete("/provisions/{name}", async (
                string name,
                ClaimsPrincipal user,
                IDbContextOutbox<ProvisioningDbContext> outbox,
                Payment.PaymentClient payments,
                ILogger<DeprovisionService> logger,
                CancellationToken ct) =>
            {
                var userId = user.GetUserId();
                var db = outbox.DbContext;

                var provision = await db.ProvisionedServices
                    .SingleOrDefaultAsync(p => p.Name == name && p.UserId == userId, ct);
                if (provision is null) return Results.NotFound();

                // Already being deleted: nothing more to do.
                if (provision.ProvisioningStatus == ProvisioningStatus.Deleting) return Results.Accepted();

                // Whether there is a subscription to stop is only known once the payment is resolved (a minute or two).
                if (provision.ProvisioningStatus == ProvisioningStatus.PendingPayment)
                    return Results.Problem(
                        "The payment for this site is still being confirmed. Please try again in a minute.",
                        statusCode: StatusCodes.Status409Conflict);

                try
                {
                    var stopped = await payments.StopSubscriptionAsync(
                        new StopSubscriptionRequest { StripeSubscriptionId = provision.StripeSubscriptionId },
                        deadline: PendingPayments.Deadline(),
                        cancellationToken: ct);

                    if (stopped.Status != SubscriptionStopped)
                        return Results.Problem(
                            $"Subscription could not be stopped ({stopped.Status}).",
                            statusCode: StatusCodes.Status502BadGateway);
                }
                catch (RpcException ex)
                {
                    logger.LogError(ex, "StopSubscription failed for provision {ProvisionId}", provision.Id);
                    return Results.Problem(
                        "Payment service is unavailable.", statusCode: StatusCodes.Status502BadGateway);
                }

                // The subscription is stopped: the site must go too, even if the client has disconnected by now.
                provision.ProvisioningStatus = ProvisioningStatus.Deleting;
                provision.UpdatedAt = DateTime.UtcNow;
                await outbox.PublishAsync(new DeprovisionService(provision.Id));
                await outbox.SaveChangesAndFlushMessagesAsync(CancellationToken.None);

                return Results.Accepted();
            })
            .RequireAuthorization();

        return app;
    }
}
