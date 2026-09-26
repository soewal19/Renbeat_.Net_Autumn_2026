namespace RoomBooking.Shared.SignalR;

/// <summary>
/// SignalR hub method names — shared between server hub and WASM client.
/// Using constants avoids typos and keeps the contract explicit.
/// </summary>
public static class ScheduleHubMethods
{
    /// <summary>Client → Server: join real-time updates for a resource.</summary>
    public const string JoinResource = nameof(JoinResource);

    /// <summary>Client → Server: leave real-time updates for a resource.</summary>
    public const string LeaveResource = nameof(LeaveResource);

    /// <summary>Server → Client: a slot was successfully booked.</summary>
    public const string SlotBooked = nameof(SlotBooked);
    public const string SlotCancelled = nameof(SlotCancelled);
}

/// <summary>
/// Payload broadcast to all clients viewing a resource schedule after a booking is committed.
/// Only published AFTER the database transaction is confirmed.
/// </summary>
public sealed record SlotBookedEvent(
    int SlotId,
    int ResourceId,
    int BookingId,
    DateTimeOffset BookedAtUtc);

public sealed record SlotCancelledEvent(int SlotId, int ResourceId, int BookingId);
