using System.Security.Claims;
using Api.Domain;
using Api.Infrastructure.Auth;
using Api.Infrastructure.Persistance;
using Microsoft.EntityFrameworkCore;

namespace Api.Features.GetProvisions;

public record ProvisionedServiceResponse(
    string Name,
    ProvisioningStatus ProvisioningStatus,
    DateTime StartDate);

public record UserProvisionsResponse(Guid UserId, List<ProvisionedServiceResponse> Services);

public static class GetProvisionsEndpoint
{
    /// <summary>GET /provisions. The calling user's WordPress sites.</summary>
    public static IEndpointRouteBuilder MapGetProvisions(this IEndpointRouteBuilder app)
    {
        app.MapGet("/provisions", async (ClaimsPrincipal user, ProvisioningDbContext db, CancellationToken ct) =>
            {
                var userId = user.GetUserId();

                var services = await db.ProvisionedServices
                    .AsNoTracking()
                    .Where(p => p.UserId == userId)
                    .OrderByDescending(p => p.StartDate)
                    .Select(p => new ProvisionedServiceResponse(p.Name, p.ProvisioningStatus, p.StartDate))
                    .ToListAsync(ct);

                return Results.Ok(new UserProvisionsResponse(userId, services));
            })
            .RequireAuthorization();

        return app;
    }
}
