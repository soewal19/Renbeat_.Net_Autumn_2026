namespace RoomBooking.Server.Infrastructure.Identity;

/// <summary>
/// Application role names. Used in [Authorize(Roles = ...)] attributes
/// and in role-based seeding at startup.
/// </summary>
public static class AppRoles
{
    public const string User = "User";
    public const string Admin = "Admin";
}
