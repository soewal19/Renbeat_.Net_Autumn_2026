using Microsoft.AspNetCore.Identity;

namespace RoomBooking.Server.Infrastructure.Persistence.Entities;

/// <summary>
/// Application user extending ASP.NET Core Identity.
/// Roles are managed via IdentityRole — do not store role info on this entity directly.
/// </summary>
public sealed class ApplicationUser : IdentityUser
{
    public string DisplayName { get; set; } = string.Empty;
    public byte[]? AvatarData { get; set; }
    public string? AvatarContentType { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;

    public ICollection<Booking> Bookings { get; set; } = new List<Booking>();
}
