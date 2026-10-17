using Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace Api.Infrastructure.Persistance;

public static class DevSeed
{
    /// <summary>Development only: inserts a few sample services when the catalogue is empty.</summary>
    public static async Task SeedAsync(ProvisioningDbContext db)
    {
        if (await db.Services.AnyAsync()) return;

        db.Services.AddRange(
            Create("postgres", "price_REPLACE_ME", "15", "16", "17"),
            Create("redis", "price_REPLACE_ME", "7.2", "7.4"),
            Create("nginx", "price_REPLACE_ME", "1.26", "1.27"));

        await db.SaveChangesAsync();
    }

    private static Service Create(string name, string stripePriceId, params string[] versions) => new()
    {
        Id = name,
        DockerImageName = name,
        StripePriceId = stripePriceId,
        Versions = versions.Select(v => new ServiceVersion { ServiceId = name, Version = v }).ToList()
    };
}
