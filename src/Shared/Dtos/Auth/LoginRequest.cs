namespace RoomBooking.Shared.Dtos.Auth;

/// <summary>Login request DTO.</summary>
public sealed record LoginRequest(
    string Email,
    string Password);
