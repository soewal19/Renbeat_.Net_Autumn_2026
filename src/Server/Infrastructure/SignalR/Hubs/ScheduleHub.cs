using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using RoomBooking.Shared.SignalR;

namespace RoomBooking.Server.Infrastructure.SignalR.Hubs;

/// <summary>
/// Real-time schedule hub.
/// Clients join resource-specific groups to receive slot booking notifications.
///
/// Group naming: "resource:{resourceId}"
///
/// RULES:
/// - Clients call JoinResource when opening a schedule view.
/// - Clients call LeaveResource when navigating away.
/// - Server broadcasts SlotBooked ONLY after a booking is committed to the database.
///   Never send a notification before persistence is confirmed.
/// </summary>
[Authorize]
public sealed class ScheduleHub : Hub
{
    private readonly ILogger<ScheduleHub> _logger;

    public ScheduleHub(ILogger<ScheduleHub> logger)
    {
        _logger = logger;
    }

    /// <summary>Subscribe to real-time updates for a specific resource's schedule.</summary>
    public async Task JoinResource(int resourceId)
    {
        var group = GetGroupName(resourceId);
        await Groups.AddToGroupAsync(Context.ConnectionId, group);
        _logger.LogDebug("Connection {ConnectionId} joined group {Group}", Context.ConnectionId, group);
    }

    /// <summary>Unsubscribe from real-time updates for a specific resource.</summary>
    public async Task LeaveResource(int resourceId)
    {
        var group = GetGroupName(resourceId);
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, group);
        _logger.LogDebug("Connection {ConnectionId} left group {Group}", Context.ConnectionId, group);
    }

    /// <summary>Returns the SignalR group name for a given resource.</summary>
    public static string GetGroupName(int resourceId) => $"resource:{resourceId}";
}
