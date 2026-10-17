using System.Security.Claims;
using Api.Domain;
using Api.Infrastructure.Auth;
using Api.Infrastructure.Persistance;
using Api.Protos;
using Grpc.Core;
using Microsoft.EntityFrameworkCore;
using Wolverine.EntityFrameworkCore;

namespace Api.Features.PostProvisions;

public record ProvisionRequest(string ServiceId, string Version);

public record ProvisionResponse(Guid ProvisionId, ProvisioningStatus ProvisioningStatus);

public static class PostProvisionsEndpoint
{
    private const string SubscriptionSuccessful = "SUCCESSFUL";

    /// <summary>
    /// POST /provisions. Asks PaymentService whether the subscription can start, then stores a Queued
    /// provision and a <see cref="ProvisionService"/> message in one transaction (Wolverine outbox).
    /// </summary>
    public static IEndpointRouteBuilder MapPostProvisions(this IEndpointRouteBuilder app)
    {
        app.MapPost("/provisions", async (
                ProvisionRequest request,
                ClaimsPrincipal user,
                IDbContextOutbox<ProvisioningDbContext> outbox,
                Payment.PaymentClient payments,
                ILogger<ProvisionRequest> logger,
                CancellationToken ct) =>
            {
                var userId = user.GetUserId();
                var db = outbox.DbContext;

                var service = await db.Services
                    .AsNoTracking()
                    .Where(s => s.Id == request.ServiceId)
                    .Select(s => new
                    {
                        s.Id,
                        s.DockerImageName,
                        s.StripePriceId,
                        HasVersion = s.Versions.Any(v => v.Version == request.Version)
                    })
                    .SingleOrDefaultAsync(ct);

                if (service is null) return Results.NotFound($"Service '{request.ServiceId}' does not exist.");
                if (!service.HasVersion)
                    return Results.NotFound($"Service '{request.ServiceId}' has no version '{request.Version}'.");

                var alreadyProvisioned = await db.ProvisionedServices.AnyAsync(p =>
                    p.UserId == userId
                    && p.ServiceId == service.Id
                    && p.ProvisioningStatus != ProvisioningStatus.Failed, ct);
                if (alreadyProvisioned) return Results.Conflict("User already has this service.");

                TryMakeSubscriptionResponse subscription;
                try
                {
                    subscription = await payments.TryMakeSubscriptionAsync(
                        new TryMakeSubscriptionRequest
                        {
                            UserId = userId.ToString(),
                            StripePriceId = service.StripePriceId,
                            ServiceId = service.Id
                        },
                        cancellationToken: ct);
                }
                catch (RpcException ex)
                {
                    logger.LogError(ex, "TryMakeSubscription failed for user {UserId}", userId);
                    return Results.Problem(
                        "Payment service is unavailable.", statusCode: StatusCodes.Status502BadGateway);
                }

                if (subscription.Status != SubscriptionSuccessful)
                    return Results.Problem(
                        $"Subscription could not be started ({subscription.Status}).",
                        statusCode: StatusCodes.Status402PaymentRequired);

                var provision = new ProvisionedService
                {
                    Id = Guid.NewGuid(),
                    UserId = userId,
                    ServiceId = service.Id,
                    ServiceName = service.DockerImageName,
                    ServiceVersion = request.Version,
                    StripeSubscriptionId = subscription.StripeSubscriptionId,
                    ProvisioningStatus = ProvisioningStatus.Queued,
                    StartDate = DateTime.UtcNow
                };
                db.ProvisionedServices.Add(provision);

                await outbox.PublishAsync(new ProvisionService(provision.Id));
                await outbox.SaveChangesAndFlushMessagesAsync(ct);

                return Results.Accepted($"/provisions/{provision.Id}",
                    new ProvisionResponse(provision.Id, provision.ProvisioningStatus));
            })
            .RequireAuthorization();

        return app;
    }
}
