using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RoomBooking.Server.Infrastructure.Persistence;
using RoomBooking.Server.Infrastructure.Identity;
using RoomBooking.Shared.Dtos.Analytics;

namespace RoomBooking.Server.Features.Analytics;

public static class AnalyticsEndpoints
{
    public static RouteGroupBuilder MapAnalyticsApi(this RouteGroupBuilder group)
    {
        var analytics = group.MapGroup("/analytics")
            .WithTags("Analytics")
            .WithDescription("Analytics and statistics endpoints. Requires Admin role.")
            .RequireAuthorization(AppRoles.Admin);

        analytics.MapGet("/dashboard", GetDashboard)
            .WithName("AnalyticsDashboard")
            .WithSummary("Get complete analytics dashboard")
            .Produces<AnalyticsDashboardDto>()
            .ProducesProblem(401);

        analytics.MapGet("/bookings", GetBookingStatistics)
            .WithName("BookingStatistics")
            .WithSummary("Get booking statistics by period")
            .Produces<BookingStatisticsDto>()
            .ProducesProblem(401);

        analytics.MapGet("/rooms/utilisation", GetRoomUtilisation)
            .WithName("RoomUtilisation")
            .WithSummary("Get room utilisation rates")
            .Produces<List<RoomUtilisationDto>>()
            .ProducesProblem(401);

        analytics.MapGet("/users/activity", GetUserActivity)
            .WithName("UserActivity")
            .WithSummary("Get user activity statistics")
            .Produces<List<UserActivityDto>>()
            .ProducesProblem(401);

        analytics.MapGet("/timeslots/popularity", GetTimeSlotPopularity)
            .WithName("TimeSlotPopularity")
            .WithSummary("Get popular time slots")
            .Produces<List<TimeSlotPopularityDto>>()
            .ProducesProblem(401);

        return group;
    }

    private static async Task<IResult> GetDashboard(AppDbContext db, CancellationToken ct, [FromQuery] string period = "Week")
    {
        var bookingStats = await GetBookingStatisticsData(period, db, ct);
        var roomUtilisation = await GetRoomUtilisationData(db, ct);
        var userActivity = await GetUserActivityData(db, ct);
        var timeSlotPopularity = await GetTimeSlotPopularityData(db, ct);

        return Results.Ok(new AnalyticsDashboardDto
        {
            BookingStatistics = bookingStats,
            RoomUtilisation = roomUtilisation,
            TopUsers = userActivity,
            PopularTimeSlots = timeSlotPopularity
        });
    }

    private static async Task<IResult> GetBookingStatistics(AppDbContext db, CancellationToken ct, [FromQuery] string period = "Week")
    {
        var stats = await GetBookingStatisticsData(period, db, ct);
        return Results.Ok(stats);
    }

    private static async Task<IResult> GetRoomUtilisation(AppDbContext db, CancellationToken ct)
    {
        var data = await GetRoomUtilisationData(db, ct);
        return Results.Ok(data);
    }

    private static async Task<IResult> GetUserActivity(AppDbContext db, CancellationToken ct)
    {
        var data = await GetUserActivityData(db, ct);
        return Results.Ok(data);
    }

    private static async Task<IResult> GetTimeSlotPopularity(AppDbContext db, CancellationToken ct)
    {
        var data = await GetTimeSlotPopularityData(db, ct);
        return Results.Ok(data);
    }

    private static async Task<BookingStatisticsDto> GetBookingStatisticsData(string period, AppDbContext db, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var startDate = period.ToLowerInvariant() switch
        {
            "day" => now.Date,
            "week" => now.Date.AddDays(-(int)now.DayOfWeek),
            "month" => new DateTime(now.Year, now.Month, 1),
            _ => now.Date.AddDays(-7)
        };

        var bookings = await db.Bookings
            .Include(b => b.TimeSlot)
            .Where(b => b.CreatedAtUtc >= startDate)
            .ToListAsync(ct);

        var bookingsByDate = bookings
            .GroupBy(b => b.CreatedAtUtc.Date.ToString("yyyy-MM-dd"))
            .Select(g => new BookingCountByDateDto
            {
                Date = g.Key,
                Count = g.Count()
            })
            .OrderBy(x => x.Date)
            .ToList();

        var totalDuration = bookings.Sum(b => (b.TimeSlot.EndUtc - b.TimeSlot.StartUtc).TotalMinutes);

        return new BookingStatisticsDto
        {
            Period = period,
            TotalBookings = bookings.Count,
            UniqueUsers = bookings.Select(b => b.UserId).Distinct().Count(),
            AverageDurationMinutes = bookings.Count > 0 ? totalDuration / bookings.Count : 0,
            BookingsByDate = bookingsByDate
        };
    }

    private static async Task<List<RoomUtilisationDto>> GetRoomUtilisationData(AppDbContext db, CancellationToken ct)
    {
        var resources = await db.Resources.ToListAsync(ct);
        var result = new List<RoomUtilisationDto>();

        foreach (var resource in resources)
        {
            var totalSlots = await db.TimeSlots
                .CountAsync(ts => ts.ResourceId == resource.Id, ct);

            var bookedSlots = await db.Bookings
                .Join(db.TimeSlots, b => b.TimeSlotId, ts => ts.Id, (b, ts) => new { b, ts })
                .CountAsync(x => x.ts.ResourceId == resource.Id, ct);

            result.Add(new RoomUtilisationDto
            {
                ResourceId = resource.Id,
                ResourceName = resource.Name,
                UtilisationPercentage = totalSlots > 0 ? (double)bookedSlots / totalSlots * 100 : 0,
                TotalSlots = totalSlots,
                BookedSlots = bookedSlots
            });
        }

        return result.OrderByDescending(r => r.UtilisationPercentage).ToList();
    }

    private static async Task<List<UserActivityDto>> GetUserActivityData(AppDbContext db, CancellationToken ct)
    {
        return await db.Bookings
            .Include(b => b.User)
            .GroupBy(b => b.UserId)
            .Select(g => new UserActivityDto
            {
                UserId = g.Key,
                UserEmail = g.First().User.Email ?? "Unknown",
                BookingCount = g.Count(),
                LastBookingDate = g.Max(b => b.CreatedAtUtc).UtcDateTime
            })
            .OrderByDescending(u => u.BookingCount)
            .Take(10)
            .ToListAsync(ct);
    }

    private static async Task<List<TimeSlotPopularityDto>> GetTimeSlotPopularityData(AppDbContext db, CancellationToken ct)
    {
        var bookings = await db.Bookings
            .Include(b => b.TimeSlot)
            .ToListAsync(ct);

        var hourGroups = bookings
            .GroupBy(b => b.TimeSlot.StartUtc.Hour)
            .Select(g => new
            {
                Hour = g.Key,
                Count = g.Count()
            })
            .ToList();

        var totalCount = bookings.Count;
        var result = new List<TimeSlotPopularityDto>();

        for (int hour = 0; hour < 24; hour++)
        {
            var group = hourGroups.FirstOrDefault(g => g.Hour == hour);
            var count = group?.Count ?? 0;
            if (count > 0)
            {
                result.Add(new TimeSlotPopularityDto
                {
                    HourRange = $"{hour:00}:00-{(hour + 1):00}:00",
                    BookingCount = count,
                    Percentage = totalCount > 0 ? (double)count / totalCount * 100 : 0
                });
            }
        }

        return result.OrderByDescending(t => t.BookingCount).ToList();
    }
}
