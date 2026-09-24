using System.Text;
using System.Text.Json;

namespace RoomBooking.Server.Features.Ai;

public sealed class SkillUploadException(string message, int statusCode = 400) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
}

/// <summary>Parses small text-only skill uploads; never writes files or executes uploaded content.</summary>
public static class SkillUploadParser
{
    public const int MaxBytes = 64 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static SkillDefinition Parse(string fileName, ReadOnlySpan<byte> content)
    {
        if (content.Length is 0 or > MaxBytes) throw new SkillUploadException("Upload exactly one non-empty file up to 64 KB.", content.Length > MaxBytes ? 413 : 400);
        var extension = Path.GetExtension(Path.GetFileName(fileName)).ToLowerInvariant();
        if (extension is not (".md" or ".txt" or ".json")) throw new SkillUploadException("Only .md, .txt, and .json files are supported.");
        string text;
        try { text = new UTF8Encoding(false, true).GetString(content); }
        catch (DecoderFallbackException) { throw new SkillUploadException("File must contain valid UTF-8 text."); }

        if (extension == ".json")
        {
            SkillDefinition? definition;
            try { definition = JsonSerializer.Deserialize<SkillDefinition>(text, JsonOptions); }
            catch (JsonException) { throw new SkillUploadException("JSON must contain a SkillDefinition object."); }
            if (definition is null) throw new SkillUploadException("JSON must contain a SkillDefinition object.");
            if (!SkillValidation.TryValidate(definition, out var errors)) throw new SkillUploadException(string.Join(" ", errors));
            return definition with { Version = 1 };
        }

        var body = text.Trim();
        if (string.IsNullOrWhiteSpace(body) || body.Length > 10000) throw new SkillUploadException("Text must contain 1 to 10000 characters.");
        var name = Path.GetFileNameWithoutExtension(fileName);
        name = new string(name.Where(c => char.IsLetterOrDigit(c) || c is ' ' or '-' or '_').ToArray()).Trim();
        if (name.Length > 120) name = name[..120];
        return new SkillDefinition(string.IsNullOrWhiteSpace(name) ? "Uploaded skill draft" : name,
            "Draft from an untrusted text upload. Review before saving.", [body], [], 1);
    }
}
