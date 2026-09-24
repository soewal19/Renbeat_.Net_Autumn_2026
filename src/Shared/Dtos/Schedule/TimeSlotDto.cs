namespace RoomBooking.Shared.Dtos.Schedule;

/// <summary>A bookable time slot for a resource.</summary>
public sealed record TimeSlotDto(
    int Id,
    int ResourceId,
    DateTimeOffset StartUtc,
    DateTimeOffset EndUtc,
    bool IsBooked,
    BookingInfoDto? Booking);

/// <summary>Compact booking info embedded in a time slot.</summary>
public sealed record BookingInfoDto(
    int BookingId,
    DateTimeOffset BookedAtUtc);
