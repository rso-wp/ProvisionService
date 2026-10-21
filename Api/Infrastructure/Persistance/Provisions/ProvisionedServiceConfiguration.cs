using Api.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Api.Infrastructure.Persistance.Provisions;

public class ProvisionedServiceConfiguration : IEntityTypeConfiguration<ProvisionedService>
{
    public void Configure(EntityTypeBuilder<ProvisionedService> builder)
    {
        builder.ToTable("ProvisionedServices");
        builder.HasKey(p => p.Id);
        builder.Property(p => p.Tenant).IsRequired().HasMaxLength(30);
        builder.Property(p => p.Name).IsRequired().HasMaxLength(63);
        builder.Property(p => p.StripeSubscriptionId).HasMaxLength(255);
        builder.Property(p => p.Url).HasMaxLength(255);
        builder.Property(p => p.ProvisioningStatus).HasConversion<string>().HasMaxLength(50);
        builder.HasIndex(p => p.UserId);
        builder.HasIndex(p => new { p.ProvisioningStatus, p.UpdatedAt });
        builder.HasIndex(p => new { p.Tenant, p.Name }).IsUnique();
        builder.HasIndex(p => p.StripeSubscriptionId).IsUnique();
    }
}

public class UserTenantConfiguration : IEntityTypeConfiguration<UserTenant>
{
    public void Configure(EntityTypeBuilder<UserTenant> builder)
    {
        builder.ToTable("UserTenants");
        builder.HasKey(t => t.UserId);
        builder.Property(t => t.UserId).ValueGeneratedNever();
        builder.Property(t => t.Tenant).IsRequired().HasMaxLength(30);
        builder.HasIndex(t => t.Tenant).IsUnique();
    }
}
