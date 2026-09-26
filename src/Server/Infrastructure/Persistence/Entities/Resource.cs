namespace RoomBooking.Server.Infrastructure.Persistence.Entities;

/// <summary>
/// A bookable meeting room or physical resource.
/// Admins manage resources; Users view and book slots on them.
/// </summary>
public sealed class Resource
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
    public byte[]? ImageData { get; set; }
    public string? ImageContentType { get; set; }

    public ICollection<TimeSlot> TimeSlots { get; set; } = new List<TimeSlot>();
}
