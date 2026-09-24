namespace RoomBooking.Server.Features.Ai;

public static class SkillValidation
{
    public static bool TryValidate(SkillDefinition definition, out IReadOnlyList<string> errors)
    {
        var issues = new List<string>();
        if (string.IsNullOrWhiteSpace(definition.Name) || definition.Name.Trim().Length > 120) issues.Add("Name is required and must be at most 120 characters.");
        if (string.IsNullOrWhiteSpace(definition.Description) || definition.Description.Trim().Length > 1000) issues.Add("Description is required and must be at most 1000 characters.");
        if (definition.Version < 1) issues.Add("Version must be at least 1.");
        if (definition.Instructions is null || definition.Instructions.Count is < 1 or > 20 || definition.Instructions.Any(x => string.IsNullOrWhiteSpace(x) || x.Length > 1000)) issues.Add("Provide 1–20 non-empty instructions, each at most 1000 characters.");
        if (definition.Examples is null || definition.Examples.Count > 10 || definition.Examples.Any(x => string.IsNullOrWhiteSpace(x) || x.Length > 500)) issues.Add("Provide at most 10 examples, each at most 500 characters.");
        errors = issues;
        return issues.Count == 0;
    }
}
