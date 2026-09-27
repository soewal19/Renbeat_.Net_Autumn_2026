namespace RoomBooking.Shared.Dtos.Analytics;

/// <summary>
/// Статистика бронирований по периодам
/// </summary>
public sealed record BookingStatisticsDto
{
    public required string Period { get; init; } // "Day", "Week", "Month"
    public required int TotalBookings { get; init; }
    public required int UniqueUsers { get; init; }
    public required double AverageDurationMinutes { get; init; }
    public required List<BookingCountByDateDto> BookingsByDate { get; init; }
}

/// <summary>
/// Количество бронирований по дате
/// </summary>
public sealed record BookingCountByDateDto
{
    public required string Date { get; init; } // ISO format: "2024-09-27"
    public required int Count { get; init; }
}

/// <summary>
/// Загрузка комнат (utilisation rate)
/// </summary>
public sealed record RoomUtilisationDto
{
    public required int ResourceId { get; init; }
    public required string ResourceName { get; init; }
    public required double UtilisationPercentage { get; init; } // 0-100
    public required int TotalSlots { get; init; }
    public required int BookedSlots { get; init; }
}

/// <summary>
/// Активность пользователей
/// </summary>
public sealed record UserActivityDto
{
    public required string UserId { get; init; }
    public required string UserEmail { get; init; }
    public required int BookingCount { get; init; }
    public required DateTime LastBookingDate { get; init; }
}

/// <summary>
/// Популярность временных слотов
/// </summary>
public sealed record TimeSlotPopularityDto
{
    public required string HourRange { get; init; } // "09:00-10:00"
    public required int BookingCount { get; init; }
    public required double Percentage { get; init; }
}

/// <summary>
/// Общая сводка аналитики
/// </summary>
public sealed record AnalyticsDashboardDto
{
    public required BookingStatisticsDto BookingStatistics { get; init; }
    public required List<RoomUtilisationDto> RoomUtilisation { get; init; }
    public required List<UserActivityDto> TopUsers { get; init; }
    public required List<TimeSlotPopularityDto> PopularTimeSlots { get; init; }
}
