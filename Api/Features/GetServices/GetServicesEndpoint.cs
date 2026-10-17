using Api.Infrastructure.Persistance;
using Microsoft.EntityFrameworkCore;

namespace Api.Features.GetServices;

public record ServiceResponse(string Id, string Name, string[] Versions);

public static class GetServicesEndpoint
{
    /// <summary>GET /services. All available docker images with their versions.</summary>
    public static IEndpointRouteBuilder MapGetServices(this IEndpointRouteBuilder app)
    {
        app.MapGet("/services", async (ProvisioningDbContext db, CancellationToken ct) =>
            {
                var services = await db.Services
                    .AsNoTracking()
                    .OrderBy(s => s.DockerImageName)
                    .Select(s => new ServiceResponse(
                        s.Id,
                        s.DockerImageName,
                        s.Versions.OrderBy(v => v.Id).Select(v => v.Version).ToArray()))
                    .ToListAsync(ct);

                return Results.Ok(services);
            })
            .RequireAuthorization();

        return app;
    }
}
