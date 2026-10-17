using System.Security.Claims;
using System.Text.RegularExpressions;
using Api.Domain;
using Api.Infrastructure.Auth;
using Api.Infrastructure.Persistance;
using Api.Protos;
using Grpc.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;
using Wolverine.EntityFrameworkCore;

namespace Api.Features.PostProvisions;

public record ProvisionRequest(string Name);

public record ProvisionResponse(Guid ProvisionId, string Name, ProvisioningStatus ProvisioningStatus);

public static partial class PostProvisionsEndpoint
{
    private const string SubscriptionSuccessful = "SUCCESSFUL";

    // A DNS label: it becomes part of the tenant's host name and helm release name.
    [GeneratedRegex("^[a-z0-9]([-a-z0-9]{0,28}[a-z0-9])?$")]
    private static partial Regex TenantName();

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
                IOptions<ProvisioningOptions> options,
                ILogger<ProvisionRequest> logger,
                CancellationToken ct) =>
            {
                if (request.Name is null || !TenantName().IsMatch(request.Name))
                    return Results.BadRequest(
                        "Name must be 1-30 characters: lowercase letters, digits and '-', not starting or ending with '-'.");

                var userId = user.GetUserId();
                var db = outbox.DbContext;

                if (await db.ProvisionedServices.AnyAsync(p => p.Name == request.Name, ct))
                    return Results.Conflict($"Name '{request.Name}' is taken.");

                TryMakeSubscriptionResponse subscription;
                try
                {
                    subscription = await payments.TryMakeSubscriptionAsync(
                        new TryMakeSubscriptionRequest
                        {
                            UserId = userId.ToString(),
                            StripePriceId = options.Value.StripePriceId,
                            ServiceId = ProvisioningOptions.ServiceId
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
                        string.IsNullOrEmpty(subscription.Reason)
                            ? $"Subscription could not be started ({subscription.Status})."
                            : subscription.Reason,
                        statusCode: StatusCodes.Status402PaymentRequired);

                var provision = new ProvisionedService
                {
                    Id = Guid.NewGuid(),
                    UserId = userId,
                    Name = request.Name,
                    StripeSubscriptionId = subscription.StripeSubscriptionId,
                    ProvisioningStatus = ProvisioningStatus.Queued,
                    StartDate = DateTime.UtcNow
                };
                db.ProvisionedServices.Add(provision);

                await outbox.PublishAsync(new ProvisionService(provision.Id));
                try
                {
                    await outbox.SaveChangesAndFlushMessagesAsync(ct);
                }
                catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
                {
                    // Lost a race for the same name after the subscription was already started.
                    logger.LogError(ex, "Name {Name} taken concurrently; subscription {Subscription} needs cancelling",
                        request.Name, subscription.StripeSubscriptionId);
                    return Results.Conflict($"Name '{request.Name}' is taken.");
                }

                return Results.Accepted($"/provisions/{provision.Id}",
                    new ProvisionResponse(provision.Id, provision.Name, provision.ProvisioningStatus));
            })
            .RequireAuthorization();

        return app;
    }
}
