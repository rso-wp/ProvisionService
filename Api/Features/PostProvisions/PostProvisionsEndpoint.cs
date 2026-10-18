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

/// <param name="Name">Site name, unique within the user's tenant: the site is served at name.tenant.baseDomain.</param>
/// <param name="PaymentMethodId">Stripe payment method (card) to charge; empty = the customer's default card.</param>
public record ProvisionRequest(string Name, string? PaymentMethodId);

public record ProvisionResponse(Guid ProvisionId, string Name, string Host, ProvisioningStatus ProvisioningStatus);

public static partial class PostProvisionsEndpoint
{
    private const string SubscriptionSuccessful = "SUCCESSFUL";

    // A DNS label: it becomes part of the site's host name.
    [GeneratedRegex("^[a-z0-9]([-a-z0-9]{0,28}[a-z0-9])?$")]
    private static partial Regex SiteName();

    /// <summary>
    /// POST /provisions. Asks PaymentService whether the subscription can start (fail fast), then stores a Queued
    /// provision and a <see cref="ProvisionService"/> message in one transaction (Wolverine outbox).
    /// All errors are ProblemDetails; <c>detail</c> is safe to show the user.
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
                if (request.Name is null || !SiteName().IsMatch(request.Name))
                    return Problem(StatusCodes.Status400BadRequest,
                        "Use lowercase letters, numbers and hyphens (not at the start or end), up to 30 characters.");

                var userId = user.GetUserId();
                var db = outbox.DbContext;

                var tenant = await db.UserTenants.FindAsync([userId], ct);
                if (tenant is null)
                    return Problem(StatusCodes.Status409Conflict,
                        "Your account is still being set up. Please try again in a few seconds.");

                if (await db.ProvisionedServices.AnyAsync(p => p.Tenant == tenant.Tenant && p.Name == request.Name, ct))
                    return Problem(StatusCodes.Status409Conflict, $"You already have a site called \"{request.Name}\".");

                TryMakeSubscriptionResponse subscription;
                try
                {
                    subscription = await payments.TryMakeSubscriptionAsync(
                        new TryMakeSubscriptionRequest
                        {
                            UserId = userId.ToString(),
                            StripePriceId = options.Value.StripePriceId,
                            ServiceId = ProvisioningOptions.ServiceId,
                            PaymentMethodId = request.PaymentMethodId ?? ""
                        },
                        cancellationToken: ct);
                }
                catch (RpcException ex)
                {
                    logger.LogError(ex, "TryMakeSubscription failed for user {UserId}", userId);
                    return Problem(StatusCodes.Status502BadGateway, "Payments are unavailable right now. Please try again.");
                }

                if (subscription.Status != SubscriptionSuccessful)
                    return Problem(StatusCodes.Status402PaymentRequired,
                        string.IsNullOrEmpty(subscription.Reason)
                            ? "The subscription could not be started."
                            : subscription.Reason);

                var provision = new ProvisionedService
                {
                    Id = Guid.NewGuid(),
                    UserId = userId,
                    Tenant = tenant.Tenant,
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
                    // Lost a race for the same name after the subscription was started: do not leave it charging.
                    logger.LogWarning(ex, "Name {Name} taken concurrently; cancelling subscription {Subscription}",
                        request.Name, subscription.StripeSubscriptionId);
                    await payments.StopSubscriptionAsync(
                        new StopSubscriptionRequest { StripeSubscriptionId = subscription.StripeSubscriptionId },
                        cancellationToken: CancellationToken.None);
                    return Problem(StatusCodes.Status409Conflict, $"You already have a site called \"{request.Name}\".");
                }

                return Results.Accepted($"/provisions/{provision.Id}",
                    new ProvisionResponse(
                        provision.Id, provision.Name, provision.Host(options.Value.BaseDomain), provision.ProvisioningStatus));
            })
            .RequireAuthorization();

        return app;
    }

    private static IResult Problem(int status, string detail) => Results.Problem(detail: detail, statusCode: status);
}
