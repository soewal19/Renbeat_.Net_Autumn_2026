using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.SignalR.Client;
using RoomBooking.Shared.Dtos.Bookings;
using RoomBooking.Shared.SignalR;

namespace RoomBooking.Client.Services;

public sealed record AdminOverview(int Resources, int TimeSlots, int Bookings, int Users, int AiSkills, string Database, bool AiConfigured, string SignalR);
public sealed record CreateTimeSlotRequest(DateTimeOffset StartUtc, DateTimeOffset EndUtc);

public interface IAdminClient
{
    Task<AdminOverview?> OverviewAsync(CancellationToken ct = default);
    Task<IReadOnlyList<BookingDto>> BookingsAsync(CancellationToken ct = default);
    Task AddTimeSlotAsync(int resourceId, CreateTimeSlotRequest request, CancellationToken ct = default);
}

public sealed class AdminClient(ApiClient api) : IAdminClient
{
    public Task<AdminOverview?> OverviewAsync(CancellationToken ct = default) => api.GetAsync<AdminOverview>("/api/admin/overview", ct);
    public async Task<IReadOnlyList<BookingDto>> BookingsAsync(CancellationToken ct = default) => await api.GetAsync<List<BookingDto>>("/api/admin/bookings", ct) ?? [];
    public async Task AddTimeSlotAsync(int resourceId, CreateTimeSlotRequest request, CancellationToken ct = default) => _ = await api.SendAsync<JsonElement>(HttpMethod.Post, $"/api/resources/{resourceId}/slots", new[] { request }, ct);
}

public sealed record DirectoryPerson(string Id, string Name, string Email, string Role, string Team, string Location, string? Avatar, string Source);
public sealed record DirectoryRoom(string Id, string Name, string Description, int Capacity, IReadOnlyList<string> Features, string Location, string? Image, string Source);
public sealed record DirectorySearchResults(IReadOnlyList<DirectoryPerson> Users, IReadOnlyList<DirectoryRoom> Rooms);

public interface IDirectoryClient
{
    Task<DirectorySearchResults> SearchAsync(string? query, CancellationToken ct = default);
}

public sealed class DirectoryClient(ApiClient api) : IDirectoryClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<DirectorySearchResults> SearchAsync(string? query, CancellationToken ct = default)
    {
        var term = query?.Trim() ?? string.Empty;
        var encoded = Uri.EscapeDataString(term);
        var database = await api.SendAsync<DirectoryDatabaseResults>(HttpMethod.Post, "/api/directory/search", new { query = term }, ct) ?? new();
        var fixture = await api.GetAsync<DirectoryFixture>("/data/directory.json", ct) ?? new();

        var localUsers = fixture.Users.Select((person, index) => new DirectoryPerson($"fixture-{index + 1}", person.Name, person.Email, person.Role, person.Team, person.Location, person.Avatar, "file"));
        var users = new Dictionary<string, DirectoryPerson>(StringComparer.OrdinalIgnoreCase);
        foreach (var person in localUsers) users[person.Email] = person;
        foreach (var person in database.Users)
        {
            var email = person.Email ?? string.Empty;
            users.TryGetValue(email, out var local);
            users[email] = new DirectoryPerson(person.Id, person.DisplayName, email, local?.Role ?? "Workspace member", local?.Team ?? "Workspace", local?.Location ?? string.Empty, local?.Avatar, "database");
        }

        var localRooms = fixture.Rooms.Select((room, index) => new DirectoryRoom($"fixture-{index + 1}", room.Name, room.Description, room.Capacity, room.Features, room.Location, room.Image, "file"));
        var rooms = new Dictionary<string, DirectoryRoom>(StringComparer.OrdinalIgnoreCase);
        foreach (var room in localRooms) rooms[room.Name] = room;
        foreach (var room in database.Rooms)
        {
            rooms.TryGetValue(room.Name, out var local);
            rooms[room.Name] = new DirectoryRoom(room.Id.ToString(), room.Name, local?.Description ?? room.Description,
                local?.Capacity ?? 0, local?.Features ?? [], local?.Location ?? string.Empty,
                local?.Image ?? room.ImageUrl, "database");
        }

        bool Match(object value) => string.IsNullOrEmpty(term) || value.ToString()?.Contains(term, StringComparison.OrdinalIgnoreCase) == true;
        var matchingUsers = users.Values.Where(person => Match(person.Name) || Match(person.Email) || Match(person.Role) || Match(person.Team) || Match(person.Location)).OrderBy(person => person.Name).ToArray();
        var matchingRooms = rooms.Values.Where(room => Match(room.Name) || Match(room.Description) || Match(room.Location) || room.Features.Any(Match)).OrderBy(room => room.Name).ToArray();
        return new DirectorySearchResults(matchingUsers, matchingRooms);
    }

    private sealed class DirectoryDatabaseResults
    {
        public List<DatabasePerson> Users { get; set; } = [];
        public List<DatabaseRoom> Rooms { get; set; } = [];
    }
    private sealed class DatabasePerson { public string Id { get; set; } = string.Empty; public string DisplayName { get; set; } = string.Empty; public string? Email { get; set; } }
    private sealed class DatabaseRoom { public int Id { get; set; } public string Name { get; set; } = string.Empty; public string Description { get; set; } = string.Empty; public bool IsActive { get; set; } public string ImageUrl { get; set; } = "/images/rooms/no_image_rooms.png"; }
    private sealed class DirectoryFixture { public List<FixturePerson> Users { get; set; } = []; public List<FixtureRoom> Rooms { get; set; } = []; }
    private sealed class FixturePerson { public string Name { get; set; } = string.Empty; public string Email { get; set; } = string.Empty; public string Role { get; set; } = string.Empty; public string Team { get; set; } = string.Empty; public string Location { get; set; } = string.Empty; public string? Avatar { get; set; } }
    private sealed class FixtureRoom { public string Name { get; set; } = string.Empty; public string Description { get; set; } = string.Empty; public int Capacity { get; set; } public List<string> Features { get; set; } = []; public string Location { get; set; } = string.Empty; public string? Image { get; set; } }
}

public interface IScheduleRealtimeClient : IAsyncDisposable
{
    bool IsConnected { get; }
    event Func<SlotBookedEvent, Task>? SlotBooked;
    event Func<SlotCancelledEvent, Task>? SlotCancelled;
    event Action? StateChanged;
    Task StartAsync(CancellationToken ct = default);
    Task StopAsync(CancellationToken ct = default);
    Task JoinResourceAsync(int resourceId, CancellationToken ct = default);
    Task LeaveResourceAsync(int resourceId, CancellationToken ct = default);
}

public sealed class ScheduleRealtimeClient(NavigationManager navigation) : IScheduleRealtimeClient
{
    private HubConnection? _connection;
    private int? _activeResourceId;
    public bool IsConnected => _connection?.State == HubConnectionState.Connected;
    public event Func<SlotBookedEvent, Task>? SlotBooked;
    public event Func<SlotCancelledEvent, Task>? SlotCancelled;
    public event Action? StateChanged;

    public async Task StartAsync(CancellationToken ct = default)
    {
        if (_connection is not null) return;
        _connection = new HubConnectionBuilder()
            .WithUrl(new Uri(new Uri(navigation.BaseUri), "hubs/schedule"))
            .WithAutomaticReconnect()
            .Build();
        _connection.On<SlotBookedEvent>(ScheduleHubMethods.SlotBooked, async message =>
        {
            if (SlotBooked is { } handler) await handler(message);
        });
        _connection.On<SlotCancelledEvent>(ScheduleHubMethods.SlotCancelled, async message =>
        {
            if (SlotCancelled is { } handler) await handler(message);
        });
        _connection.Reconnecting += _ => { StateChanged?.Invoke(); return Task.CompletedTask; };
        _connection.Reconnected += async _ =>
        {
            if (_activeResourceId is int resourceId)
                await _connection.InvokeAsync(ScheduleHubMethods.JoinResource, resourceId);
            StateChanged?.Invoke();
        };
        _connection.Closed += _ => { StateChanged?.Invoke(); return Task.CompletedTask; };
        await _connection.StartAsync(ct);
        StateChanged?.Invoke();
    }

    public async Task JoinResourceAsync(int resourceId, CancellationToken ct = default)
    {
        _activeResourceId = resourceId;
        if (_connection is not null && IsConnected) await _connection.InvokeAsync(ScheduleHubMethods.JoinResource, resourceId, ct);
    }
    public async Task LeaveResourceAsync(int resourceId, CancellationToken ct = default)
    {
        if (_activeResourceId == resourceId) _activeResourceId = null;
        if (_connection is not null && IsConnected) await _connection.InvokeAsync(ScheduleHubMethods.LeaveResource, resourceId, ct);
    }
    public async Task StopAsync(CancellationToken ct = default)
    {
        if (_connection is not null)
        {
            await _connection.StopAsync(ct);
            await _connection.DisposeAsync();
            _connection = null;
        }
        _activeResourceId = null;
        StateChanged?.Invoke();
    }
    public async ValueTask DisposeAsync()
    {
        if (_connection is not null) await _connection.DisposeAsync();
    }
}
