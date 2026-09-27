namespace RoomBooking.Client.Services;

public sealed record AiChatRequestClient(string Message, string? TimeZoneId = null);
public sealed record AiBookingReceiptClient(int BookingId, int TimeSlotId, int ResourceId, string ResourceName, DateTimeOffset StartUtc, DateTimeOffset EndUtc);
public sealed record AiChatResponseClient(string Answer, object? BookingProposal = null, AiBookingReceiptClient? BookingReceipt = null);

public interface IAiChatClient
{
    Task<AiChatResponseClient?> SendAsync(string message, string? timeZoneId, CancellationToken ct = default);
    Task<bool> IsAvailableAsync(CancellationToken ct = default);
}

public sealed class AiChatClient(ApiClient api) : IAiChatClient
{
    public Task<AiChatResponseClient?> SendAsync(string message, string? timeZoneId, CancellationToken ct = default) =>
        api.SendAsync<AiChatResponseClient>(HttpMethod.Post, "/api/ai/chat", new AiChatRequestClient(message, timeZoneId), ct);

    public async Task<bool> IsAvailableAsync(CancellationToken ct = default) =>
        (await api.GetAsync<AiStatusResponse>("/api/ai/status", ct))?.Available == true;

    private sealed record AiStatusResponse(bool Available);
}
