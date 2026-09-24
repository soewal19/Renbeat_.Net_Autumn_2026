using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using RoomBooking.Server.Infrastructure.Persistence.Entities;

namespace RoomBooking.Server.Infrastructure.Persistence.Configurations;

public sealed class TimeSlotConfiguration : IEntityTypeConfiguration<TimeSlot>
{
    public void Configure(EntityTypeBuilder<TimeSlot> builder)
    {
        builder.HasKey(ts => ts.Id);
        builder.Property(ts => ts.StartUtc).IsRequired();
        builder.Property(ts => ts.EndUtc).IsRequired();

        // Prevent duplicate time slots for the same resource at the same start time.
        builder.HasIndex(ts => new { ts.ResourceId, ts.StartUtc, ts.EndUtc }).IsUnique()
               .HasDatabaseName("IX_TimeSlots_ResourceId_StartUtc_EndUtc_Unique");

        builder.HasOne(ts => ts.Resource)
               .WithMany(r => r.TimeSlots)
               .HasForeignKey(ts => ts.ResourceId)
               .OnDelete(DeleteBehavior.Cascade);

        // The 1:1 Booking relationship and unique FK are configured in BookingConfiguration
        // to keep the concurrency invariant in one place.
    }
}
