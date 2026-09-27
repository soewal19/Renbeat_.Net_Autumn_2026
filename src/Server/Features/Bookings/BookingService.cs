using System.Diagnostics;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using RoomBooking.Server.Infrastructure.Persistence;
using RoomBooking.Server.Infrastructure.Persistence.Entities;
using RoomBooking.Server.Infrastructure.SignalR.Hubs;
using RoomBooking.Shared.Dtos.Bookings;
using RoomBooking.Shared.SignalR;

namespace RoomBooking.Server.Features.Bookings;

public enum BookingFailure { None, SlotNotFound, Conflict }
public sealed record BookingResult(BookingFailure Failure, BookingDto? Booking = null)
{
    public bool Succeeded => Failure == BookingFailure.None && Booking is not null;
}

/// <summary>Single booking write path for user and AI requests. The unique database index is authoritative.</summary>
public sealed class BookingService(AppDbContext db, ILogger<BookingService> logger, IHubContext<ScheduleHub>? hub = null)
{
    public async Task<BookingResult> CreateAsync(int slotId, string userId, bool isAiGenerated, CancellationToken ct)
    {
        var timer = Stopwatch.StartNew();
        var slot = await db.TimeSlots.AsNoTracking()
            .Where(x => x.Id == slotId && x.Resource.IsActive && x.StartUtc > DateTimeOffset.UtcNow)
            .Select(x => new { x.Id, x.ResourceId, ResourceName = x.Resource.Name, x.StartUtc, x.EndUtc })
            .FirstOrDefaultAsync(ct);
        if (slot is null) return new BookingResult(BookingFailure.SlotNotFound);

        var email = await db.Users.AsNoTracking().Where(x => x.Id == userId).Select(x => x.Email).FirstOrDefaultAsync(ct) ?? string.Empty;
        var booking = new Booking { TimeSlotId = slot.Id, UserId = userId, CreatedAtUtc = DateTimeOffset.UtcNow, IsAiGenerated = isAiGenerated };
        db.Bookings.Add(booking);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException ex) when (DbConcurrencyHelper.IsUniqueConstraintViolation(ex))
        {
            logger.LogInformation("Booking attempt {Outcome} resourceId {ResourceId} slotId {SlotId} durationMs {DurationMs}", "conflict", slot.ResourceId, slot.Id, timer.ElapsedMilliseconds);
            return new BookingResult(BookingFailure.Conflict);
        }

        var dto = new BookingDto(booking.Id, slot.Id, slot.StartUtc, slot.EndUtc, slot.ResourceId, slot.ResourceName, userId, email, booking.CreatedAtUtc, isAiGenerated);
        logger.LogInformation("Booking attempt {Outcome} resourceId {ResourceId} slotId {SlotId} durationMs {DurationMs} isAiGenerated {IsAiGenerated}", "success", slot.ResourceId, slot.Id, timer.ElapsedMilliseconds, isAiGenerated);
        if (hub is not null)
        {
            try
            {
                var evt = new SlotBookedEvent(slot.Id, slot.ResourceId, booking.Id, booking.CreatedAtUtc);
                await hub.Clients.Group(ScheduleHub.GetGroupName(slot.ResourceId)).SendAsync(ScheduleHubMethods.SlotBooked, evt, ct);
            }
            catch (Exception ex)
            { logger.LogWarning(ex, "Realtime notification failed after booking {BookingId} was committed", booking.Id); }
        }
        return new BookingResult(BookingFailure.None, dto);
    }
}
