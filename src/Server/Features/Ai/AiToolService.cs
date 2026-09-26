using System.Globalization;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using RoomBooking.Server.Infrastructure.Persistence;
using RoomBooking.Server.Infrastructure.Persistence.Entities;
using RoomBooking.Server.Infrastructure.SignalR.Hubs;
using RoomBooking.Shared.SignalR;

namespace RoomBooking.Server.Features.Ai;

/// <summary>Explicit tool allowlist. AI can inspect data and prepare a booking proposal, but never writes bookings.</summary>
public sealed class AiToolService(AppDbContext db, ILogger<AiToolService> logger, IHubContext<ScheduleHub>? hub = null) : IAiToolService
{
    public async Task<string> ExecuteAsync(string toolName, string argumentsJson, string userId, CancellationToken cancellationToken, bool allowAutonomousBooking = false, string userTimeZone = "UTC")
    {
        var timer = Stopwatch.StartNew();
        try
        {
            using var args = JsonDocument.Parse(argumentsJson);
            var root = args.RootElement;
            object result = toolName switch
            {
                "get_resources" => await GetResourcesAsync(cancellationToken),
                "get_schedule" => await GetScheduleAsync(root, cancellationToken),
                "get_my_bookings" => await GetMyBookingsAsync(userId, cancellationToken),
                "get_booking_policy" => new
                {
                    rule = "A time slot may have at most one booking.",
                    conflict = "If another user books the slot first, the request returns HTTP 409 Conflict.",
                    recovery = "Refresh the schedule and choose another available slot.",
                    cancellation = "Users can cancel their own future bookings from My bookings."
                },
                "propose_booking" => await ProposeBookingAsync(root, cancellationToken),
                "book_slot" when allowAutonomousBooking && !string.IsNullOrWhiteSpace(userId) => await BookSlotAsync(root, userId, userTimeZone, cancellationToken),
                "book_slot" => new { success = false, error = "Booking requires a clear user instruction and an authenticated account." },
                _ => throw new InvalidOperationException("The requested AI tool is not allowed.")
            };
            logger.LogInformation("AI tool {ToolName} durationMs {DurationMs} outcome {Outcome}", toolName, timer.ElapsedMilliseconds, "success");
            return JsonSerializer.Serialize(result);
        }
        catch
        {
            logger.LogWarning("AI tool {ToolName} durationMs {DurationMs} outcome {Outcome}", toolName, timer.ElapsedMilliseconds, "failure");
            throw;
        }
    }

    private async Task<object> GetResourcesAsync(CancellationToken ct) => await db.Resources.AsNoTracking()
        .Where(x => x.IsActive).OrderBy(x => x.Name)
        .Select(x => new { x.Id, x.Name, x.Description }).ToListAsync(ct);

    private async Task<object> GetScheduleAsync(JsonElement args, CancellationToken ct)
    {
        if (!args.TryGetProperty("resourceId", out var idNode) || !idNode.TryGetInt32(out var resourceId) || resourceId < 1 ||
            !args.TryGetProperty("date", out var dateNode) || !DateOnly.TryParse(dateNode.GetString(), CultureInfo.InvariantCulture, out var date))
            throw new ArgumentException("get_schedule requires a positive resourceId and ISO date (YYYY-MM-DD).");

        var exists = await db.Resources.AsNoTracking().AnyAsync(x => x.Id == resourceId && x.IsActive, ct);
        if (!exists) return new { error = "Resource not found or unavailable." };
        var start = new DateTimeOffset(date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc));
        var end = start.AddDays(1);
        return await db.TimeSlots.AsNoTracking().Where(x => x.ResourceId == resourceId && x.StartUtc >= start && x.StartUtc < end)
            .OrderBy(x => x.StartUtc)
            .Select(x => new { x.Id, x.StartUtc, x.EndUtc, IsBooked = x.Booking != null })
            .ToListAsync(ct);
    }

    private async Task<object> GetMyBookingsAsync(string userId, CancellationToken ct) => await db.Bookings.AsNoTracking()
        .Where(x => x.UserId == userId).OrderByDescending(x => x.CreatedAtUtc)
        .Select(x => new { x.Id, Resource = x.TimeSlot.Resource.Name, x.TimeSlot.StartUtc, x.TimeSlot.EndUtc, x.CreatedAtUtc })
        .Take(100).ToListAsync(ct);

    private async Task<object> ProposeBookingAsync(JsonElement args, CancellationToken ct)
    {
        if (!args.TryGetProperty("timeSlotId", out var idNode) || !idNode.TryGetInt32(out var slotId) || slotId < 1)
            throw new ArgumentException("propose_booking requires a positive timeSlotId.");
        var now = DateTimeOffset.UtcNow;
        var slot = await db.TimeSlots.AsNoTracking()
            .Where(x => x.Id == slotId && x.StartUtc > now && x.Resource.IsActive && x.Booking == null)
            .Select(x => new { x.Id, x.ResourceId, ResourceName = x.Resource.Name, x.StartUtc, x.EndUtc })
            .FirstOrDefaultAsync(ct);
        return slot is null
            ? new { available = false, error = "That slot is no longer available. Find another free slot." }
            : new { available = true, timeSlotId = slot.Id, resourceId = slot.ResourceId, resourceName = slot.ResourceName, startUtc = slot.StartUtc, endUtc = slot.EndUtc };
    }

    private async Task<object> BookSlotAsync(JsonElement args, string userId, string userTimeZone, CancellationToken ct)
    {
        if (!args.TryGetProperty("resourceId", out var resourceNode) || !resourceNode.TryGetInt32(out var resourceId) || resourceId < 1 ||
            !args.TryGetProperty("date", out var dateNode) || !DateOnly.TryParseExact(dateNode.GetString(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ||
            !args.TryGetProperty("startTime", out var timeNode) || !TimeOnly.TryParseExact(timeNode.GetString(), "H:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var startTime) && !TimeOnly.TryParseExact(timeNode.GetString(), "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out startTime))
            throw new ArgumentException("book_slot requires a positive resourceId, ISO date (YYYY-MM-DD), and local startTime (HH:mm).");

        TimeZoneInfo zone;
        try { zone = TimeZoneInfo.FindSystemTimeZoneById(userTimeZone); }
        catch (TimeZoneNotFoundException) { zone = TimeZoneInfo.Utc; }
        catch (InvalidTimeZoneException) { zone = TimeZoneInfo.Utc; }
        var localStart = date.ToDateTime(startTime, DateTimeKind.Unspecified);
        if (zone.IsInvalidTime(localStart) || zone.IsAmbiguousTime(localStart))
            return new { success = false, error = "The requested local time is ambiguous due to a daylight saving change. Please specify another time." };
        var exactStartUtc = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(localStart, zone), TimeSpan.Zero);

        var now = DateTimeOffset.UtcNow;
        var slot = await db.TimeSlots.Include(x => x.Resource)
            .FirstOrDefaultAsync(x => x.ResourceId == resourceId && x.StartUtc == exactStartUtc && x.StartUtc > now && x.Resource.IsActive, ct);
        if (slot is null) return new { success = false, error = "No exact future slot matches that room, local date, and start time. No booking was created." };

        var booking = new Booking
        {
            TimeSlotId = slot.Id,
            UserId = userId,
            CreatedAtUtc = now,
            IsAiGenerated = true
        };
        db.Bookings.Add(booking);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (DbConcurrencyHelper.IsUniqueConstraintViolation(ex))
        {
            logger.LogInformation("AI booking conflict for slot {SlotId}", slot.Id);
            return new { success = false, conflict = true, error = "That slot was just booked by someone else. No booking was created." };
        }

        logger.LogInformation("AI created booking {BookingId} for slot {SlotId} and user {UserId}", booking.Id, slot.Id, userId);
        if (hub is not null)
        {
            var eventData = new SlotBookedEvent(slot.Id, slot.ResourceId, booking.Id, booking.CreatedAtUtc);
            try { await hub.Clients.Group(ScheduleHub.GetGroupName(slot.ResourceId)).SendAsync(ScheduleHubMethods.SlotBooked, eventData, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning("Could not publish realtime update for committed AI booking {BookingId}", booking.Id);
            }
        }

        return new { success = true, bookingId = booking.Id, timeSlotId = slot.Id, resourceId = slot.ResourceId, resourceName = slot.Resource.Name, startUtc = slot.StartUtc, endUtc = slot.EndUtc };
    }
}
