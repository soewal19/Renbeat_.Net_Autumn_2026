namespace RoomBooking.Server.Infrastructure.Persistence.Entities;

/// <summary>
/// A confirmed booking for a single time slot.
///
/// CONCURRENCY INVARIANT:
/// The UNIQUE database index on TimeSlotId (configured in BookingConfiguration)
/// is the sole mechanism preventing double-booking.
///
/// Application code MUST NOT:
/// - Use SELECT-then-INSERT to check availability before booking.
/// - Rely on C# locks, static fields, or in-memory mutexes.
/// - Assume a single application instance.
///
/// The correct flow is:
///   1. Attempt INSERT directly.
///   2. Catch DbUpdateException caused by unique constraint violation.
///   3. Return HTTP 409 Conflict with a ProblemDetails body.
/// </summary>
public sealed class Booking
{
    public int Id { get; set; }

    /// <summary>
    /// Foreign key to TimeSlot. Has a UNIQUE database index via BookingConfiguration.
    /// This column is the database-enforced booking uniqueness guarantee.
    /// </summary>
    public int TimeSlotId { get; set; }

    /// <summary>Identity user ID of the person who made the booking.</summary>
    public string UserId { get; set; } = string.Empty;

    /// <summary>When the booking was created, always in UTC.</summary>
    public DateTimeOffset CreatedAtUtc { get; set; }

    /// <summary>Indicates that the authenticated user confirmed this booking through the AI assistant.</summary>
    public bool IsAiGenerated { get; set; }

    public TimeSlot TimeSlot { get; set; } = null!;
    public ApplicationUser User { get; set; } = null!;
}
