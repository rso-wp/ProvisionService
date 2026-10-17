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
        builder.Property(p => p.Name).IsRequired().HasMaxLength(63);
        builder.Property(p => p.StripeSubscriptionId).IsRequired().HasMaxLength(255);
        builder.Property(p => p.ProvisioningStatus).HasConversion<string>().HasMaxLength(50);
        builder.HasIndex(p => p.UserId);
        builder.HasIndex(p => p.Name).IsUnique();
        builder.HasIndex(p => p.StripeSubscriptionId).IsUnique();
    }
}
