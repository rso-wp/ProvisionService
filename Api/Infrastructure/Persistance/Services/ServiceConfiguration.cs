using Api.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Api.Infrastructure.Persistance.Services;

public class ServiceConfiguration : IEntityTypeConfiguration<Service>
{
    public void Configure(EntityTypeBuilder<Service> builder)
    {
        builder.ToTable("Services");
        builder.HasKey(s => s.Id);
        builder.Property(s => s.Id).HasMaxLength(100);
        builder.Property(s => s.DockerImageName).IsRequired().HasMaxLength(255);
        builder.Property(s => s.StripePriceId).IsRequired().HasMaxLength(255);
        builder.HasIndex(s => s.DockerImageName).IsUnique();
    }
}

public class ServiceVersionConfiguration : IEntityTypeConfiguration<ServiceVersion>
{
    public void Configure(EntityTypeBuilder<ServiceVersion> builder)
    {
        builder.ToTable("ServiceVersions");
        builder.HasKey(v => v.Id);
        builder.Property(v => v.ServiceId).IsRequired().HasMaxLength(100);
        builder.Property(v => v.Version).IsRequired().HasMaxLength(100);
        builder.HasIndex(v => new { v.ServiceId, v.Version }).IsUnique();
        builder.HasOne<Service>()
            .WithMany(s => s.Versions)
            .HasForeignKey(v => v.ServiceId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
