namespace RoomBooking.Shared.Dtos.Bookings;

/// <summary>Request to book a time slot.</summary>
public sealed record CreateBookingRequest(int TimeSlotId);

/// <summary>Confirmed booking DTO.</summary>
public sealed record BookingDto(
    int Id,
    int TimeSlotId,
    DateTimeOffset SlotStartUtc,
    DateTimeOffset SlotEndUtc,
    int ResourceId,
    string ResourceName,
    string UserId,
    string UserEmail,
    DateTimeOffset CreatedAtUtc);
