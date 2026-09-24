using System.Globalization;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RoomBooking.Server.Infrastructure.Persistence;

namespace RoomBooking.Server.Features.Ai;

/// <summary>Explicit read-only tool allowlist. This service is the only bridge from AI tools to application data.</summary>
public sealed class AiToolService(AppDbContext db, ILogger<AiToolService> logger) : IAiToolService
{
    public async Task<string> ExecuteAsync(string toolName, string argumentsJson, string userId, CancellationToken cancellationToken)
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
                    cancellation = "Bookings cannot currently be cancelled by users."
                },
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
}
