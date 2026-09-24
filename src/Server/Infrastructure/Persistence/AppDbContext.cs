using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using RoomBooking.Server.Infrastructure.Persistence.Configurations;
using RoomBooking.Server.Infrastructure.Persistence.Entities;

namespace RoomBooking.Server.Infrastructure.Persistence;

/// <summary>
/// Main application database context.
/// Extends IdentityDbContext to include ASP.NET Core Identity tables.
/// </summary>
public sealed class AppDbContext : IdentityDbContext<ApplicationUser>
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<Resource> Resources => Set<Resource>();
    public DbSet<TimeSlot> TimeSlots => Set<TimeSlot>();
    public DbSet<Booking> Bookings => Set<Booking>();
    public DbSet<AiSkill> AiSkills => Set<AiSkill>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.ApplyConfiguration(new ResourceConfiguration());
        builder.ApplyConfiguration(new TimeSlotConfiguration());
        builder.ApplyConfiguration(new BookingConfiguration());
        builder.ApplyConfiguration(new AiSkillConfiguration());
    }
}
