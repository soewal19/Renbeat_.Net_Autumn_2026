namespace RoomBooking.Server.Infrastructure.Persistence.Entities;

/// <summary>
/// A fixed, bookable time window belonging to a resource.
/// Time slots are pre-defined by admins and cannot overlap for the same resource.
/// </summary>
public sealed class TimeSlot
{
    public int Id { get; set; }
    public int ResourceId { get; set; }

    /// <summary>Slot start time in UTC.</summary>
    public DateTimeOffset StartUtc { get; set; }

    /// <summary>Slot end time in UTC.</summary>
    public DateTimeOffset EndUtc { get; set; }

    public Resource Resource { get; set; } = null!;

    /// <summary>
    /// The booking for this slot, if any. Null means the slot is available.
    /// The UNIQUE constraint on Booking.TimeSlotId ensures at most one booking per slot.
    /// </summary>
    public Booking? Booking { get; set; }
}
