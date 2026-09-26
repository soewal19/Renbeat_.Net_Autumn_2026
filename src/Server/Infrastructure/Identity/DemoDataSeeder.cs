using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using RoomBooking.Server.Infrastructure.Persistence;
using RoomBooking.Server.Infrastructure.Persistence.Entities;

namespace RoomBooking.Server.Infrastructure.Identity;

/// <summary>Idempotently imports the local demo directory into the database when explicitly enabled.</summary>
public static class DemoDataSeeder
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    public static async Task SeedAsync(
        AppDbContext db,
        UserManager<ApplicationUser> userManager,
        RoleManager<IdentityRole> roleManager,
        IWebHostEnvironment environment,
        ILogger logger,
        CancellationToken cancellationToken = default)
    {
        if (db.Database.IsSqlServer())
        {
            var strategy = db.Database.CreateExecutionStrategy();
            await strategy.ExecuteAsync(() => SeedCoreAsync(db, userManager, roleManager, environment, logger, cancellationToken));
            return;
        }

        await SeedCoreAsync(db, userManager, roleManager, environment, logger, cancellationToken);
    }

    private static async Task SeedCoreAsync(
        AppDbContext db,
        UserManager<ApplicationUser> userManager,
        RoleManager<IdentityRole> roleManager,
        IWebHostEnvironment environment,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        IDbContextTransaction? transaction = null;
        if (db.Database.IsSqlServer())
            transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await using var transactionScope = transaction;
        if (transaction is not null)
        {
            await db.Database.ExecuteSqlRawAsync(
                "DECLARE @lockResult int; EXEC @lockResult = sp_getapplock @Resource = 'RoomBooking.DemoDataSeed', @LockMode = 'Exclusive', @LockOwner = 'Transaction', @LockTimeout = 60000; IF @lockResult < 0 THROW 51000, 'Could not acquire demo seed lock.', 1;",
                cancellationToken);
        }
        var path = Path.Combine(environment.WebRootPath, "data", "directory.json");
        if (!File.Exists(path))
        {
            logger.LogWarning("Demo directory seed file was not found at {Path}.", path);
            if (transaction is not null) await transaction.CommitAsync(cancellationToken);
            return;
        }

        await using var stream = File.OpenRead(path);
        var seed = await JsonSerializer.DeserializeAsync<DemoDirectorySeed>(stream, JsonOptions, cancellationToken)
            ?? throw new InvalidDataException("Demo directory seed file is empty or invalid.");

        if (!await roleManager.RoleExistsAsync(AppRoles.User))
            await roleManager.CreateAsync(new IdentityRole(AppRoles.User));

        var usersAdded = 0;
        foreach (var item in seed.Users)
        {
            if (string.IsNullOrWhiteSpace(item.Email) || string.IsNullOrWhiteSpace(item.Name)) continue;
            if (await userManager.FindByEmailAsync(item.Email) is not null) continue;

            // No password is set: demo directory identities cannot sign in.
            var user = new ApplicationUser
            {
                UserName = item.Email,
                Email = item.Email,
                DisplayName = item.Name,
                EmailConfirmed = true,
                CreatedAtUtc = DateTimeOffset.UtcNow
            };
            var created = await userManager.CreateAsync(user);
            if (!created.Succeeded)
                throw new InvalidOperationException($"Could not seed demo user {item.Email}: {string.Join(", ", created.Errors.Select(error => error.Description))}");
            var assigned = await userManager.AddToRoleAsync(user, AppRoles.User);
            if (!assigned.Succeeded)
                throw new InvalidOperationException($"Could not assign the User role to demo user {item.Email}.");
            usersAdded++;
        }

        var roomsAdded = 0;
        foreach (var item in seed.Rooms)
        {
            if (string.IsNullOrWhiteSpace(item.Name)) continue;
            var resource = await db.Resources.FirstOrDefaultAsync(room => room.Name == item.Name, cancellationToken);
            if (resource is null)
            {
                var features = item.Features.Count == 0 ? string.Empty : $" Equipment: {string.Join(", ", item.Features)}.";
                resource = new Resource
                {
                    Name = item.Name,
                    Description = $"{item.Description} Capacity: {item.Capacity} people. Location: {item.Location}.{features}",
                    IsActive = true,
                    CreatedAtUtc = DateTimeOffset.UtcNow,
                    UpdatedAtUtc = DateTimeOffset.UtcNow
                };
                db.Resources.Add(resource);
                await db.SaveChangesAsync(cancellationToken);
                roomsAdded++;
            }

            if (!await db.TimeSlots.AnyAsync(slot => slot.ResourceId == resource.Id, cancellationToken))
            {
                var firstDay = DateTimeOffset.UtcNow.Date.AddDays(1);
                for (var hour = 9; hour <= 13; hour += 2)
                {
                    var start = new DateTimeOffset(firstDay.AddHours(hour), TimeSpan.Zero);
                    db.TimeSlots.Add(new TimeSlot { ResourceId = resource.Id, StartUtc = start, EndUtc = start.AddHours(1) });
                }
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        if (transaction is not null) await transaction.CommitAsync(cancellationToken);
        logger.LogInformation("Demo directory seed complete: added {UsersAdded} users and {RoomsAdded} rooms.", usersAdded, roomsAdded);
    }
}

public sealed class DemoDirectorySeed
{
    public List<DemoDirectoryUser> Users { get; set; } = [];
    public List<DemoDirectoryRoom> Rooms { get; set; } = [];
}

public sealed class DemoDirectoryUser
{
    public string Name { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
}

public sealed class DemoDirectoryRoom
{
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public int Capacity { get; set; }
    public string Location { get; set; } = string.Empty;
    public List<string> Features { get; set; } = [];
}
