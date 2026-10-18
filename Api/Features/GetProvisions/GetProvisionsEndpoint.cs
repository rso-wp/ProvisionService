using System.Security.Claims;
using Api.Domain;
using Api.Infrastructure.Auth;
using Api.Infrastructure.Persistance;
using Api.Features.PostProvisions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Api.Features.GetProvisions;

public record ProvisionedServiceResponse(
    string Name,
    string Host,
    ProvisioningStatus ProvisioningStatus,
    DateTime StartDate,
    string? Url);

public record UserProvisionsResponse(Guid UserId, List<ProvisionedServiceResponse> Services);

public static class GetProvisionsEndpoint
{
    /// <summary>GET /provisions. The calling user's WordPress sites.</summary>
    public static IEndpointRouteBuilder MapGetProvisions(this IEndpointRouteBuilder app)
    {
        app.MapGet("/provisions", async (
                ClaimsPrincipal user, ProvisioningDbContext db, IOptions<ProvisioningOptions> options, CancellationToken ct) =>
            {
                var userId = user.GetUserId();
                var baseDomain = options.Value.BaseDomain;

                var services = await db.ProvisionedServices
                    .AsNoTracking()
                    .Where(p => p.UserId == userId)
                    .OrderByDescending(p => p.StartDate)
                    .Select(p => new ProvisionedServiceResponse(
                        p.Name, p.Name + "." + p.Tenant + "." + baseDomain, p.ProvisioningStatus, p.StartDate, p.Url))
                    .ToListAsync(ct);

                return Results.Ok(new UserProvisionsResponse(userId, services));
            })
            .RequireAuthorization();

        return app;
    }
}
