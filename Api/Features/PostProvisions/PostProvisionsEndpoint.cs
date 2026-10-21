using System.Security.Claims;
using System.Text.RegularExpressions;
using Api.Domain;
using Api.Features.Tenants;
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
    // A DNS label: it becomes part of the site's host name.
    [GeneratedRegex("^[a-z0-9]([-a-z0-9]{0,28}[a-z0-9])?$")]
    private static partial Regex SiteName();

    /// <summary>
    /// POST /provisions. Reserves the name (a PendingPayment row), asks PaymentService to start the subscription (fail fast),
    /// then makes the site Queued and stores a <see cref="ProvisionService"/> message in one transaction (Wolverine outbox).
    /// The provision id is the payment's request id, so a lost answer is resolved later instead of charging twice.
    /// All errors are ProblemDetails; <c>detail</c> is safe to show the user.
    /// </summary>
    public static IEndpointRouteBuilder MapPostProvisions(this IEndpointRouteBuilder app)
    {
        app.MapPost("/provisions", async (
                ProvisionRequest request,
                ClaimsPrincipal user,
                IDbContextOutbox<ProvisioningDbContext> outbox,
                UserTenants tenants,
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

                var tenant = await tenants.FindAsync(userId, ct);
                if (tenant is null)
                    return Problem(StatusCodes.Status409Conflict,
                        "Your account is still being set up. Please try again in a few seconds.");

                if (await db.ProvisionedServices.AnyAsync(p => p.Tenant == tenant && p.Name == request.Name, ct))
                    return NameTaken(request.Name);

                // 1. Reserve the name before anything is charged: a concurrent request for the same name fails here.
                var now = DateTime.UtcNow;
                var provision = new ProvisionedService
                {
                    Id = Guid.NewGuid(),
                    UserId = userId,
                    Tenant = tenant,
                    Name = request.Name,
                    ProvisioningStatus = ProvisioningStatus.PendingPayment,
                    StartDate = now,
                    UpdatedAt = now
                };
                db.ProvisionedServices.Add(provision);
                try
                {
                    await db.SaveChangesAsync(ct);
                }
                catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
                {
                    return NameTaken(request.Name);
                }

                // 2. The subscription. From here on the request's cancellation is not passed on: a client that disconnects
                //    must not leave a payment without its site.
                TryMakeSubscriptionResponse answer;
                try
                {
                    answer = await payments.TryMakeSubscriptionAsync(
                        new TryMakeSubscriptionRequest
                        {
                            UserId = userId.ToString(),
                            StripePriceId = options.Value.StripePriceId,
                            ServiceId = ProvisioningOptions.ServiceId,
                            PaymentMethodId = request.PaymentMethodId ?? "",
                            RequestId = provision.Id.ToString()
                        },
                        deadline: PendingPayments.Deadline(),
                        cancellationToken: CancellationToken.None);
                }
                catch (RpcException ex)
                {
                    // Unknown outcome: the site stays PendingPayment and ProvisioningReconciler finds out what happened.
                    logger.LogError(ex, "TryMakeSubscription for provision {ProvisionId} got no answer", provision.Id);
                    return PaymentUnconfirmed();
                }

                // 3. Queued + the provisioning command, or the reservation is released.
                await PendingPayments.ApplyAsync(outbox, provision, answer);

                return answer.Status switch
                {
                    PendingPayments.Successful => Results.Accepted($"/provisions/{provision.Id}",
                        new ProvisionResponse(
                            provision.Id, provision.Name, provision.Host(options.Value.BaseDomain), provision.ProvisioningStatus)),
                    PendingPayments.Failed => Problem(StatusCodes.Status402PaymentRequired,
                        string.IsNullOrEmpty(answer.Reason) ? "The subscription could not be started." : answer.Reason),
                    _ => PaymentUnconfirmed()
                };
            })
            .RequireAuthorization();

        return app;
    }

    private static IResult PaymentUnconfirmed() => Problem(StatusCodes.Status502BadGateway,
        "We could not confirm your payment yet. If it went through, the site is set up in a few minutes; " +
        "otherwise you are not charged.");

    private static IResult NameTaken(string name) =>
        Problem(StatusCodes.Status409Conflict, $"You already have a site called \"{name}\".");

    private static IResult Problem(int status, string detail) => Results.Problem(detail: detail, statusCode: status);
}
