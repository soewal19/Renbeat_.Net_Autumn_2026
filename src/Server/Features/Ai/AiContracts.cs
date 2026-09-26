namespace RoomBooking.Server.Features.Ai;

public sealed record AiChatRequest(string Message, string? TimeZoneId = null);
public sealed record AiChatResponse(string Answer, AiBookingProposal? BookingProposal = null, AiBookingReceipt? BookingReceipt = null);
public sealed record AiBookingProposal(int TimeSlotId, int ResourceId, string ResourceName, DateTimeOffset StartUtc, DateTimeOffset EndUtc);
public sealed record AiBookingReceipt(int BookingId, int TimeSlotId, int ResourceId, string ResourceName, DateTimeOffset StartUtc, DateTimeOffset EndUtc);
public sealed record AiChatResult(string Answer, AiBookingProposal? BookingProposal = null, AiBookingReceipt? BookingReceipt = null);
public sealed record SkillDefinition(string Name, string Description, IReadOnlyList<string> Instructions, IReadOnlyList<string> Examples, int Version = 1);
public sealed record SkillWriteRequest(string Name, string Description, IReadOnlyList<string> Instructions, IReadOnlyList<string>? Examples);
public sealed record GenerateSkillRequest(string Description);
public sealed record SkillDto(int Id, string Name, string Description, IReadOnlyList<string> Instructions, IReadOnlyList<string> Examples, int Version, bool IsActive, DateTimeOffset CreatedAtUtc, DateTimeOffset UpdatedAtUtc);

public interface IAiAssistant
{
    bool IsAvailable { get; }
    Task<AiChatResult> ChatAsync(string message, string userId, CancellationToken cancellationToken, string userTimeZone = "UTC");
    Task<SkillDefinition> GenerateSkillAsync(string description, CancellationToken cancellationToken);
}

public interface IAiToolService
{
    Task<string> ExecuteAsync(string toolName, string argumentsJson, string userId, CancellationToken cancellationToken, bool allowAutonomousBooking = false, string userTimeZone = "UTC");
}
