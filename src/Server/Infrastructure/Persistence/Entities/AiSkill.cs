namespace RoomBooking.Server.Infrastructure.Persistence.Entities;

/// <summary>Persisted, non-executable AI guidance. Instructions are always treated as untrusted data.</summary>
public sealed class AiSkill
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string InstructionsJson { get; set; } = "[]";
    public string ExamplesJson { get; set; } = "[]";
    public int Version { get; set; } = 1;
    public bool IsActive { get; set; }
    public string CreatedByUserId { get; set; } = string.Empty;
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
}
