using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Net.Http;
using Microsoft.AspNetCore.Components.Forms;
using RoomBooking.Shared.Dtos.Auth;
using RoomBooking.Shared.Dtos.Bookings;
using RoomBooking.Shared.Dtos.Resources;
using RoomBooking.Shared.Dtos.Schedule;
using RoomBooking.Shared.Dtos.Analytics;

namespace RoomBooking.Client.Services;

public interface IAuthClient
{
    Task<UserDto?> MeAsync(CancellationToken ct = default);
    Task<UserDto?> LoginAsync(LoginRequest request, CancellationToken ct = default);
    Task<UserDto?> RegisterAsync(RegisterRequest request, CancellationToken ct = default);
    Task LogoutAsync(CancellationToken ct = default);
    Task<UserDto?> UpdateProfileAsync(UpdateProfileRequest request, CancellationToken ct = default);
    Task ChangePasswordAsync(ChangePasswordRequest request, CancellationToken ct = default);
    Task<UserDto?> UploadAvatarAsync(IBrowserFile file, CancellationToken ct = default);
    Task RemoveAvatarAsync(CancellationToken ct = default);
}

public sealed class AuthClient(ApiClient api) : IAuthClient
{
    public Task<UserDto?> MeAsync(CancellationToken ct = default) => api.GetAsync<UserDto>("/api/auth/me", ct);
    public Task<UserDto?> LoginAsync(LoginRequest request, CancellationToken ct = default) => api.SendAsync<UserDto>(HttpMethod.Post, "/api/auth/login", request, ct);
    public Task<UserDto?> RegisterAsync(RegisterRequest request, CancellationToken ct = default) => api.SendAsync<UserDto>(HttpMethod.Post, "/api/auth/register", request, ct);
    public async Task LogoutAsync(CancellationToken ct = default) => _ = await api.SendAsync<JsonElement>(HttpMethod.Post, "/api/auth/logout", new { }, ct);
    public Task<UserDto?> UpdateProfileAsync(UpdateProfileRequest request, CancellationToken ct = default) => api.SendAsync<UserDto>(HttpMethod.Put, "/api/auth/profile", request, ct);
    public async Task ChangePasswordAsync(ChangePasswordRequest request, CancellationToken ct = default) => _ = await api.SendAsync<JsonElement>(HttpMethod.Post, "/api/auth/password", request, ct);
    public async Task<UserDto?> UploadAvatarAsync(IBrowserFile file, CancellationToken ct = default)
    {
        using var form = new MultipartFormDataContent();
        await using var stream = file.OpenReadStream(10 * 1024 * 1024, ct);
        using var content = new StreamContent(stream);
        content.Headers.ContentType = new MediaTypeHeaderValue(file.ContentType);
        form.Add(content, "file", file.Name);
        return await api.SendAsync<UserDto>(HttpMethod.Post, "/api/auth/me/avatar", form, ct);
    }
    public async Task RemoveAvatarAsync(CancellationToken ct = default) => _ = await api.SendAsync<JsonElement>(HttpMethod.Delete, "/api/auth/me/avatar", null, ct);
}

public interface IResourceClient
{
    Task<IReadOnlyList<ResourceDto>> ListAsync(CancellationToken ct = default);
    Task<ResourceDto?> CreateAsync(CreateResourceRequest request, CancellationToken ct = default);
    Task UpdateAsync(int id, UpdateResourceRequest request, CancellationToken ct = default);
    Task UploadImageAsync(int id, IBrowserFile file, CancellationToken ct = default);
    Task DeleteAsync(int id, CancellationToken ct = default);
}

public sealed class ResourceClient(ApiClient api) : IResourceClient
{
    public async Task<IReadOnlyList<ResourceDto>> ListAsync(CancellationToken ct = default) => await api.GetAsync<List<ResourceDto>>("/api/resources", ct) ?? [];
    public Task<ResourceDto?> CreateAsync(CreateResourceRequest request, CancellationToken ct = default) => api.SendAsync<ResourceDto>(HttpMethod.Post, "/api/resources", request, ct);
    public async Task UpdateAsync(int id, UpdateResourceRequest request, CancellationToken ct = default) => _ = await api.SendAsync<JsonElement>(HttpMethod.Put, $"/api/resources/{id}", request, ct);
    public async Task UploadImageAsync(int id, IBrowserFile file, CancellationToken ct = default)
    {
        using var form = new MultipartFormDataContent();
        await using var stream = file.OpenReadStream(10 * 1024 * 1024, ct);
        using var content = new StreamContent(stream);
        content.Headers.ContentType = new MediaTypeHeaderValue(file.ContentType);
        form.Add(content, "file", file.Name);
        _ = await api.SendAsync<JsonElement>(HttpMethod.Post, $"/api/resources/{id}/image", form, ct);
    }
    public async Task DeleteAsync(int id, CancellationToken ct = default) => _ = await api.SendAsync<JsonElement>(HttpMethod.Delete, $"/api/resources/{id}", null, ct);
}

public interface IScheduleClient
{
    Task<IReadOnlyList<TimeSlotDto>> GetAsync(int resourceId, DateOnly date, CancellationToken ct = default);
}

public sealed class ScheduleClient(ApiClient api) : IScheduleClient
{
    public async Task<IReadOnlyList<TimeSlotDto>> GetAsync(int resourceId, DateOnly date, CancellationToken ct = default) =>
        await api.GetAsync<List<TimeSlotDto>>($"/api/resources/{resourceId}/schedule?date={date:yyyy-MM-dd}", ct) ?? [];
}

public interface IBookingClient
{
    Task<BookingDto?> CreateAsync(CreateBookingRequest request, CancellationToken ct = default);
    Task<IReadOnlyList<BookingDto>> MineAsync(CancellationToken ct = default);
    Task CancelAsync(int bookingId, CancellationToken ct = default);
}

public sealed class BookingClient(ApiClient api) : IBookingClient
{
    public Task<BookingDto?> CreateAsync(CreateBookingRequest request, CancellationToken ct = default) => api.SendAsync<BookingDto>(HttpMethod.Post, "/api/bookings", request, ct);
    public async Task<IReadOnlyList<BookingDto>> MineAsync(CancellationToken ct = default) => await api.GetAsync<List<BookingDto>>("/api/bookings/me", ct) ?? [];
    public async Task CancelAsync(int bookingId, CancellationToken ct = default) => _ = await api.SendAsync<JsonElement>(HttpMethod.Delete, $"/api/bookings/{bookingId}", null, ct);
}

public sealed record UpdateProfileRequest(string DisplayName, string? PhoneNumber);
public sealed record ChangePasswordRequest(string CurrentPassword, string NewPassword);

public interface IAnalyticsClient
{
    Task<AnalyticsDashboardDto?> GetDashboardAsync(string period = "Week", CancellationToken ct = default);
    Task<BookingStatisticsDto?> GetBookingStatisticsAsync(string period = "Week", CancellationToken ct = default);
    Task<IReadOnlyList<RoomUtilisationDto>> GetRoomUtilisationAsync(CancellationToken ct = default);
    Task<IReadOnlyList<UserActivityDto>> GetUserActivityAsync(CancellationToken ct = default);
    Task<IReadOnlyList<TimeSlotPopularityDto>> GetTimeSlotPopularityAsync(CancellationToken ct = default);
}

public sealed class AnalyticsClient(ApiClient api) : IAnalyticsClient
{
    public async Task<AnalyticsDashboardDto?> GetDashboardAsync(string period = "Week", CancellationToken ct = default) =>
        await api.GetAsync<AnalyticsDashboardDto>($"/api/analytics/dashboard?period={period}", ct);

    public async Task<BookingStatisticsDto?> GetBookingStatisticsAsync(string period = "Week", CancellationToken ct = default) =>
        await api.GetAsync<BookingStatisticsDto>($"/api/analytics/bookings?period={period}", ct);

    public async Task<IReadOnlyList<RoomUtilisationDto>> GetRoomUtilisationAsync(CancellationToken ct = default) =>
        await api.GetAsync<List<RoomUtilisationDto>>("/api/analytics/rooms/utilisation", ct) ?? [];

    public async Task<IReadOnlyList<UserActivityDto>> GetUserActivityAsync(CancellationToken ct = default) =>
        await api.GetAsync<List<UserActivityDto>>("/api/analytics/users/activity", ct) ?? [];

    public async Task<IReadOnlyList<TimeSlotPopularityDto>> GetTimeSlotPopularityAsync(CancellationToken ct = default) =>
        await api.GetAsync<List<TimeSlotPopularityDto>>("/api/analytics/timeslots/popularity", ct) ?? [];
}
