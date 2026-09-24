namespace RoomBooking.Shared.Dtos.Resources;

/// <summary>Request to create a new resource.</summary>
public sealed record CreateResourceRequest(
    string Name,
    string? Description);

/// <summary>Request to update an existing resource.</summary>
public sealed record UpdateResourceRequest(
    string Name,
    string? Description,
    bool IsActive);
