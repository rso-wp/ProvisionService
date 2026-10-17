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
        builder.Property(p => p.ServiceId).IsRequired().HasMaxLength(100);
        builder.Property(p => p.ServiceName).IsRequired().HasMaxLength(255);
        builder.Property(p => p.ServiceVersion).IsRequired().HasMaxLength(100);
        builder.Property(p => p.StripeSubscriptionId).IsRequired().HasMaxLength(255);
        builder.Property(p => p.ProvisioningStatus).HasConversion<string>().HasMaxLength(50);
        builder.HasIndex(p => p.UserId);
        builder.HasIndex(p => p.StripeSubscriptionId).IsUnique();
        builder.HasOne<Service>().WithMany().HasForeignKey(p => p.ServiceId);
    }
}
