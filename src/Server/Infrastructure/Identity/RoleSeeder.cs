using Microsoft.AspNetCore.Identity;
using RoomBooking.Server.Infrastructure.Identity;
using RoomBooking.Server.Infrastructure.Persistence.Entities;

namespace RoomBooking.Server.Infrastructure.Identity;

/// <summary>
/// Seeds application roles and an initial Admin user at startup.
/// Only runs if the roles / admin user do not already exist.
/// </summary>
public static class RoleSeeder
{
    public static async Task SeedAsync(
        RoleManager<IdentityRole> roleManager,
        UserManager<ApplicationUser> userManager,
        IConfiguration configuration,
        ILogger logger)
    {
        // Ensure roles exist
        foreach (var roleName in new[] { AppRoles.User, AppRoles.Admin })
        {
            if (!await roleManager.RoleExistsAsync(roleName))
            {
                var result = await roleManager.CreateAsync(new IdentityRole(roleName));
                if (result.Succeeded)
                    logger.LogInformation("Created role: {Role}", roleName);
                else
                    logger.LogError("Failed to create role {Role}: {Errors}", roleName,
                        string.Join(", ", result.Errors.Select(e => e.Description)));
            }
        }

        // Seed admin user from configuration (only if not exists)
        var adminEmail = configuration["Seed:AdminEmail"];
        var adminPassword = configuration["Seed:AdminPassword"];

        if (string.IsNullOrWhiteSpace(adminEmail) || string.IsNullOrWhiteSpace(adminPassword))
        {
            logger.LogWarning("Initial admin seeding was skipped because Seed:AdminEmail and Seed:AdminPassword are not configured.");
            return;
        }

        var existingAdmin = await userManager.FindByEmailAsync(adminEmail);
        if (existingAdmin is null)
        {
            var admin = new ApplicationUser
            {
                UserName = adminEmail,
                Email = adminEmail,
                DisplayName = "System Administrator",
                EmailConfirmed = true,
                CreatedAtUtc = DateTimeOffset.UtcNow
            };

            var createResult = await userManager.CreateAsync(admin, adminPassword);
            if (createResult.Succeeded)
            {
                await userManager.AddToRoleAsync(admin, AppRoles.Admin);
                logger.LogInformation("Seeded admin user: {Email}", adminEmail);
            }
            else
            {
                logger.LogError("Failed to seed admin user: {Errors}",
                    string.Join(", ", createResult.Errors.Select(e => e.Description)));
            }
        }
    }
}
