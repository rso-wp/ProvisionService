using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;

namespace Api.Infrastructure.Auth;

public static class AuthExtensions
{
    /// <summary>
    /// Bearer validation of UserService's tokens. The signing key comes from UserService's discovery document
    /// (`Auth:MetadataAddress`, cluster-internal http) and is refreshed when it changes.
    /// </summary>
    public static IServiceCollection AddJwtAuth(this IServiceCollection services, IConfiguration configuration)
    {
        var section = configuration.GetSection("Auth");
        string Required(string key) => section[key] is { Length: > 0 } value
            ? value
            : throw new InvalidOperationException($"'Auth:{key}' is not configured.");

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
                options.MetadataAddress = Required("MetadataAddress");
                options.RequireHttpsMetadata = false; // UserService is reached inside the cluster / on localhost
                options.MapInboundClaims = false; // keep raw claim names (sub, email, ...)
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidIssuer = Required("Issuer"),
                    ValidAudience = Required("Audience"),
                    ValidAlgorithms = [SecurityAlgorithms.RsaSha256]
                };
            });
        services.AddAuthorization();

        return services;
    }

    public static Guid GetUserId(this ClaimsPrincipal principal) =>
        Guid.TryParse(principal.FindFirstValue("sub"), out var id)
            ? id
            : throw new InvalidOperationException("Token has no valid 'sub' claim.");
}
