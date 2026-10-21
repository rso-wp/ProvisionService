using System.Net;
using Api.Domain;
using Api.Infrastructure.Persistance;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Api.Features.Tenants;

/// <summary>
/// A user's tenant slug: the local copy (from <see cref="TenantAssignedConsumer"/>), or, when that has not arrived or is
/// gone (the NATS stream only keeps 7 days), UserService's cluster-internal endpoint, whose answer becomes the local copy.
/// </summary>
public class UserTenants(ProvisioningDbContext db, HttpClient userService, ILogger<UserTenants> logger)
{
    private record TenantResponse(string Tenant);

    /// <summary>Null while the user has no tenant yet (profile not filled in) or UserService cannot be asked.</summary>
    public async Task<string?> FindAsync(Guid userId, CancellationToken ct)
    {
        if (await db.UserTenants.FindAsync([userId], ct) is { } local) return local.Tenant;

        string? tenant;
        try
        {
            using var response = await userService.GetAsync($"internal/users/{userId}/tenant", ct);
            if (response.StatusCode == HttpStatusCode.NotFound) return null;
            response.EnsureSuccessStatusCode();
            tenant = (await response.Content.ReadFromJsonAsync<TenantResponse>(ct))?.Tenant;
        }
        catch (Exception ex) when (ex is HttpRequestException || (ex is TaskCanceledException && !ct.IsCancellationRequested))
        {
            logger.LogWarning(ex, "Could not ask UserService for the tenant of user {UserId}", userId);
            return null;
        }
        if (string.IsNullOrEmpty(tenant)) return null;

        var copy = db.UserTenants.Add(new UserTenant { UserId = userId, Tenant = tenant });
        try
        {
            await db.SaveChangesAsync(ct);
            logger.LogInformation("User {UserId} has tenant {Tenant} (asked UserService)", userId, tenant);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // TenantAssigned arrived in the meantime.
            copy.State = EntityState.Detached;
        }
        return tenant;
    }
}

public static class UserTenantsExtensions
{
    /// <summary>
    /// <c>UserService:Url</c>, by default the host of <c>Auth:MetadataAddress</c> (UserService's discovery document, which
    /// is cluster-internal too).
    /// </summary>
    public static IServiceCollection AddUserTenants(this IServiceCollection services, IConfiguration configuration)
    {
        var url = configuration["UserService:Url"] is { Length: > 0 } configured
            ? configured
            : configuration["Auth:MetadataAddress"] is { Length: > 0 } metadata
                ? new Uri(metadata).GetLeftPart(UriPartial.Authority)
                : throw new InvalidOperationException("'UserService:Url' (or 'Auth:MetadataAddress') is not configured.");

        services.AddHttpClient<UserTenants>(client =>
        {
            client.BaseAddress = new Uri(url.TrimEnd('/') + "/");
            client.Timeout = TimeSpan.FromSeconds(5);
        });

        return services;
    }
}
