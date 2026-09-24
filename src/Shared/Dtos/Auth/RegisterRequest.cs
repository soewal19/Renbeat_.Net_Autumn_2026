namespace RoomBooking.Shared.Dtos.Auth;

/// <summary>Registration request DTO.</summary>
public sealed record RegisterRequest(
    string Email,
    string Password,
    string DisplayName);
