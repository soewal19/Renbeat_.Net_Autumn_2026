namespace RoomBooking.Shared.Dtos.Resources;

/// <summary>Resource (meeting room) DTO.</summary>
public sealed record ResourceDto(
    int Id,
    string Name,
    string Description,
    bool IsActive,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);
