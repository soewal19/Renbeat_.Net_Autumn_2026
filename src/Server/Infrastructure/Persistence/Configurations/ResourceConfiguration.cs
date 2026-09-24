using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using RoomBooking.Server.Infrastructure.Persistence.Entities;

namespace RoomBooking.Server.Infrastructure.Persistence.Configurations;

public sealed class ResourceConfiguration : IEntityTypeConfiguration<Resource>
{
    public void Configure(EntityTypeBuilder<Resource> builder)
    {
        builder.HasKey(r => r.Id);
        builder.Property(r => r.Name).IsRequired().HasMaxLength(200);
        builder.Property(r => r.Description).HasMaxLength(2000);

        builder.HasMany(r => r.TimeSlots)
               .WithOne(ts => ts.Resource)
               .HasForeignKey(ts => ts.ResourceId)
               .OnDelete(DeleteBehavior.Cascade);
    }
}
