using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RoomBooking.Server.Infrastructure.Persistence;
using RoomBooking.Server.Infrastructure.Persistence.Entities;

namespace RoomBooking.Server.Features.Ai;

public interface IAiSkillService
{
    Task<IReadOnlyList<string>> GetActiveInstructionsAsync(CancellationToken ct);
    Task<IReadOnlyList<SkillDto>> ListAsync(bool includeInactive, CancellationToken ct);
    Task<SkillDto?> GetAsync(int id, CancellationToken ct);
    Task<SkillDto> CreateAsync(SkillDefinition definition, string createdBy, CancellationToken ct);
    Task<SkillDto?> UpdateAsync(int id, SkillDefinition definition, CancellationToken ct);
    Task<bool> SetActiveAsync(int id, bool active, CancellationToken ct);
    Task<bool> DeleteAsync(int id, CancellationToken ct);
}

public sealed class AiSkillService(AppDbContext db) : IAiSkillService
{
    public async Task<IReadOnlyList<string>> GetActiveInstructionsAsync(CancellationToken ct)
    {
        var skills = await db.AiSkills.AsNoTracking().Where(x => x.IsActive).OrderBy(x => x.Name)
            .Select(x => new { x.Name, x.InstructionsJson }).ToListAsync(ct);
        return skills.Select(x => $"{x.Name}: {x.InstructionsJson}").ToList();
    }

    public async Task<IReadOnlyList<SkillDto>> ListAsync(bool includeInactive, CancellationToken ct)
    {
        var query = db.AiSkills.AsNoTracking();
        if (!includeInactive) query = query.Where(x => x.IsActive);
        var skills = await query.OrderBy(x => x.Name).ToListAsync(ct);
        return skills.Select(ToDto).ToList();
    }

    public async Task<SkillDto?> GetAsync(int id, CancellationToken ct)
    {
        var skill = await db.AiSkills.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        return skill is null ? null : ToDto(skill);
    }

    public async Task<SkillDto> CreateAsync(SkillDefinition definition, string createdBy, CancellationToken ct)
    {
        if (!SkillValidation.TryValidate(definition, out var errors)) throw new ArgumentException(string.Join(" ", errors));
        var now = DateTimeOffset.UtcNow;
        var skill = new AiSkill
        {
            Name = definition.Name.Trim(), Description = definition.Description.Trim(),
            InstructionsJson = JsonSerializer.Serialize(definition.Instructions), ExamplesJson = JsonSerializer.Serialize(definition.Examples),
            Version = 1, IsActive = false, CreatedByUserId = createdBy, CreatedAtUtc = now, UpdatedAtUtc = now
        };
        db.AiSkills.Add(skill);
        await db.SaveChangesAsync(ct);
        return ToDto(skill);
    }

    public async Task<SkillDto?> UpdateAsync(int id, SkillDefinition definition, CancellationToken ct)
    {
        if (!SkillValidation.TryValidate(definition, out var errors)) throw new ArgumentException(string.Join(" ", errors));
        var skill = await db.AiSkills.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (skill is null) return null;
        skill.Name = definition.Name.Trim(); skill.Description = definition.Description.Trim();
        skill.InstructionsJson = JsonSerializer.Serialize(definition.Instructions); skill.ExamplesJson = JsonSerializer.Serialize(definition.Examples);
        skill.Version++; skill.IsActive = false; skill.UpdatedAtUtc = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return ToDto(skill);
    }

    public async Task<bool> SetActiveAsync(int id, bool active, CancellationToken ct)
    {
        var skill = await db.AiSkills.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (skill is null) return false;
        skill.IsActive = active; skill.UpdatedAtUtc = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct); return true;
    }

    public async Task<bool> DeleteAsync(int id, CancellationToken ct)
    {
        var skill = await db.AiSkills.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (skill is null) return false;
        db.AiSkills.Remove(skill); await db.SaveChangesAsync(ct); return true;
    }

    private static SkillDto ToDto(AiSkill skill) => new(skill.Id, skill.Name, skill.Description,
        JsonSerializer.Deserialize<string[]>(skill.InstructionsJson) ?? [], JsonSerializer.Deserialize<string[]>(skill.ExamplesJson) ?? [],
        skill.Version, skill.IsActive, skill.CreatedAtUtc, skill.UpdatedAtUtc);
}
