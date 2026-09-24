using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using RoomBooking.Server.Infrastructure.Persistence.Entities;

namespace RoomBooking.Server.Infrastructure.Persistence.Configurations;

/// <summary>
/// EF Core configuration for Booking.
///
/// CRITICAL: The unique index on TimeSlotId defined here is the database-enforced
/// concurrency guard that prevents double-booking. This index MUST remain.
/// Do not remove or alter it without updating the concurrency strategy.
/// See docs/adr/001-concurrency-strategy.md for the architectural decision record.
/// </summary>
public sealed class BookingConfiguration : IEntityTypeConfiguration<Booking>
{
    public void Configure(EntityTypeBuilder<Booking> builder)
    {
        builder.HasKey(b => b.Id);

        // === THE CONCURRENCY INVARIANT ===
        // One TimeSlot can have AT MOST one Booking.
        // The database enforces this via this unique index.
        // The application catches violation and returns HTTP 409 Conflict.
        // Never remove this index.
        builder.HasIndex(b => b.TimeSlotId)
               .IsUnique()
               .HasDatabaseName("IX_Bookings_TimeSlotId_Unique");

        builder.Property(b => b.UserId).IsRequired();
        builder.Property(b => b.CreatedAtUtc).IsRequired();

        builder.HasOne(b => b.User)
               .WithMany(u => u.Bookings)
               .HasForeignKey(b => b.UserId)
               .OnDelete(DeleteBehavior.Restrict);
    }
}
