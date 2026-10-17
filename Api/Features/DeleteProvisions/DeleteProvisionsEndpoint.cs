using System.Security.Claims;
using Api.Domain;
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

                try
                {
                    var stopped = await payments.StopSubscriptionAsync(
                        new StopSubscriptionRequest { StripeSubscriptionId = provision.StripeSubscriptionId },
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

                provision.ProvisioningStatus = ProvisioningStatus.Deleting;
                await outbox.PublishAsync(new DeprovisionService(provision.Id));
                await outbox.SaveChangesAndFlushMessagesAsync(ct);

                return Results.Accepted();
            })
            .RequireAuthorization();

        return app;
    }
}
