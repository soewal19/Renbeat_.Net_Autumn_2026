namespace RoomBooking.Shared.Dtos.Auth;

/// <summary>Authenticated user info returned by GET /api/auth/me.</summary>
public sealed record UserDto(
    string Id,
    string Email,
    string DisplayName,
    IReadOnlyList<string> Roles);
