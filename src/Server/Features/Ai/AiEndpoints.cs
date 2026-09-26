using System.Diagnostics;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace RoomBooking.Server.Features.Ai;

public static class AiEndpoints
{
    private const long MaxUploadBytes = 64 * 1024;

    public static RouteGroupBuilder MapAiApi(this RouteGroupBuilder group)
    {
        var ai = group.MapGroup("/ai").WithTags("AI").WithDescription("Requires an authenticated Identity cookie. The optional Groq provider runs only on the server; provider failures return a safe unavailable response.").RequireAuthorization();
        ai.MapPost("/chat", Chat).RequireRateLimiting("ai").WithName("AiChat")
            .WithSummary("Ask the optional meeting-room AI assistant")
            .WithDescription("Requires authentication. Uses allowlisted data tools and enables autonomous booking only for an explicit booking instruction with an exact matching future slot. Returns 503 when Groq is not configured or unavailable.")
            .Produces<AiChatResponse>().ProducesValidationProblem().ProducesProblem(StatusCodes.Status401Unauthorized).ProducesProblem(StatusCodes.Status429TooManyRequests).ProducesProblem(StatusCodes.Status503ServiceUnavailable);
        ai.MapGet("/status", (IAiAssistant assistant) => Results.Ok(new { available = assistant.IsAvailable }))
            .WithName("AiStatus").WithSummary("Check whether the optional AI provider is configured");

        var skills = ai.MapGroup("/skills").WithTags("AI Skills").WithDescription("Authenticated users can list and use active skills. Create, edit, delete, upload, generation, activation and deactivation require the Admin role.");
        skills.MapGet("/", ListSkills).WithName("ListAiSkills").WithSummary("List active skills, or all skills for administrators");
        skills.MapGet("/{id:int}", GetSkill).WithName("GetAiSkill").WithSummary("Get an active skill, or any skill for administrators").Produces<SkillDto>().ProducesProblem(404);
        skills.MapPost("/", CreateSkill).RequireAuthorization("Admin").WithName("CreateAiSkill").WithSummary("Create an inactive skill (admin only)").Produces<SkillDto>(201).ProducesValidationProblem();
        skills.MapPut("/{id:int}", UpdateSkill).RequireAuthorization("Admin").WithName("UpdateAiSkill").WithSummary("Update a skill (admin only)").Produces<SkillDto>().ProducesValidationProblem().ProducesProblem(404);
        skills.MapDelete("/{id:int}", DeleteSkill).RequireAuthorization("Admin").WithName("DeleteAiSkill").WithSummary("Delete a skill (admin only)").Produces(204).ProducesProblem(404);
        skills.MapPost("/{id:int}/activate", (int id, IAiSkillService service, CancellationToken ct) => SetSkillActive(id, true, service, ct)).RequireAuthorization("Admin").WithName("ActivateAiSkill").WithSummary("Activate an approved skill (admin only)").Produces(204).ProducesProblem(404);
        skills.MapPost("/{id:int}/deactivate", (int id, IAiSkillService service, CancellationToken ct) => SetSkillActive(id, false, service, ct)).RequireAuthorization("Admin").WithName("DeactivateAiSkill").WithSummary("Deactivate a skill (admin only)").Produces(204).ProducesProblem(404);
        skills.MapPost("/generate", GenerateSkill).RequireAuthorization("Admin").RequireRateLimiting("ai").WithName("GenerateAiSkill").WithSummary("Generate an editable, unsaved skill draft (admin only)").Produces<SkillDefinition>().ProducesValidationProblem().ProducesProblem(429).ProducesProblem(503);
        skills.MapPost("/upload", UploadSkill).RequireAuthorization("Admin").WithName("UploadAiSkill").WithSummary("Validate a text or JSON skill file and return an unsaved draft (admin only)").WithDescription("Send multipart/form-data with one file field named 'file'. The request must have a same-origin Origin or Referer header. Supports UTF-8 .md, .txt, and .json only, up to 64 KB.").Accepts<IFormFile>("multipart/form-data").Produces<SkillDefinition>().ProducesValidationProblem().ProducesProblem(403).ProducesProblem(413);
        return group;
    }

    private static async Task<IResult> Chat(AiChatRequest request, IAiAssistant assistant, ICurrentUserService currentUser,
        ILoggerFactory loggerFactory, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Message) || request.Message.Length > 2000)
        {
            loggerFactory.CreateLogger("AiOperations").LogInformation("AI operation {Operation} outcome {Outcome} failure {FailureType}", "chat", "validation_failure", "message_validation");
            return Validation("message", "Message must contain 1 to 2000 characters.");
        }
        if (!assistant.IsAvailable) return Results.Problem(statusCode: 503, title: "AI assistant unavailable", detail: "The optional AI provider is not configured.");
        var timer = Stopwatch.StartNew();
        try
        {
            var timeZone = ResolveTimeZone(request.TimeZoneId);
            var result = await assistant.ChatAsync(request.Message.Trim(), currentUser.UserId!, ct, timeZone);
            loggerFactory.CreateLogger("AiOperations").LogInformation("AI operation {Operation} provider {Provider} durationMs {DurationMs} outcome {Outcome}", "chat", "Groq", timer.ElapsedMilliseconds, "success");
            return Results.Ok(new AiChatResponse(result.Answer, result.BookingProposal, result.BookingReceipt));
        }
        catch (AiProviderUnavailableException ex)
        {
            loggerFactory.CreateLogger("AiOperations").LogWarning("AI operation {Operation} provider {Provider} durationMs {DurationMs} outcome {Outcome} failure {FailureType}", "chat", "Groq", timer.ElapsedMilliseconds, "failure", ex.GetType().Name);
            return Results.Problem(statusCode: 503, title: "AI assistant unavailable", detail: "The AI service could not complete this request. Core booking features remain available.");
        }
        catch (ArgumentException)
        {
            return Results.Problem(statusCode: 502, title: "AI tool request invalid", detail: "The assistant returned an invalid tool request.");
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException)
        {
            loggerFactory.CreateLogger("AiOperations").LogWarning("AI response validation failed with {FailureType}", ex.GetType().Name);
            return Results.Problem(statusCode: 503, title: "AI assistant unavailable", detail: "The AI service returned a response the application could not validate.");
        }
    }

    private static async Task<IResult> ListSkills(HttpContext http, IAiSkillService service, CancellationToken ct)
        => Results.Ok(await service.ListAsync(http.User.IsInRole("Admin"), ct));

    private static async Task<IResult> GetSkill(int id, HttpContext http, IAiSkillService service, CancellationToken ct)
    {
        var skill = await service.GetAsync(id, ct);
        return skill is null || (!skill.IsActive && !http.User.IsInRole("Admin")) ? Results.NotFound() : Results.Ok(skill);
    }

    private static async Task<IResult> CreateSkill(SkillWriteRequest request, HttpContext http, IAiSkillService service, CancellationToken ct)
    {
        var definition = ToDefinition(request);
        if (!SkillValidation.TryValidate(definition, out var errors)) return Validation(errors);
        try
        {
            var saved = await service.CreateAsync(definition, http.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)!.Value, ct);
            return Results.Created($"/api/ai/skills/{saved.Id}", saved);
        }
        catch (DbUpdateException) { return Results.Conflict(new ProblemDetails { Status = 409, Title = "A skill with this name already exists." }); }
    }

    private static async Task<IResult> UpdateSkill(int id, SkillWriteRequest request, IAiSkillService service, CancellationToken ct)
    {
        var definition = ToDefinition(request);
        if (!SkillValidation.TryValidate(definition, out var errors)) return Validation(errors);
        try
        {
            var updated = await service.UpdateAsync(id, definition, ct);
            return updated is null ? Results.NotFound() : Results.Ok(updated);
        }
        catch (DbUpdateException) { return Results.Conflict(new ProblemDetails { Status = 409, Title = "A skill with this name already exists." }); }
    }

    private static async Task<IResult> DeleteSkill(int id, IAiSkillService service, CancellationToken ct)
        => await service.DeleteAsync(id, ct) ? Results.NoContent() : Results.NotFound();

    private static async Task<IResult> SetSkillActive(int id, bool active, IAiSkillService service, CancellationToken ct)
        => await service.SetActiveAsync(id, active, ct) ? Results.NoContent() : Results.NotFound();

    private static async Task<IResult> GenerateSkill(GenerateSkillRequest request, IAiAssistant assistant, ILoggerFactory loggerFactory, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Description) || request.Description.Length > 2000)
        {
            loggerFactory.CreateLogger("AiOperations").LogInformation("AI operation {Operation} outcome {Outcome} failure {FailureType}", "generate_skill", "validation_failure", "description_validation");
            return Validation("description", "Description must contain 1 to 2000 characters.");
        }
        if (!assistant.IsAvailable) return Results.Problem(statusCode: 503, title: "AI assistant unavailable", detail: "The optional AI provider is not configured.");
        var timer = Stopwatch.StartNew();
        try
        {
            var draft = await assistant.GenerateSkillAsync(request.Description.Trim(), ct);
            if (!SkillValidation.TryValidate(draft, out var errors))
            {
                loggerFactory.CreateLogger("AiOperations").LogWarning("AI operation {Operation} provider {Provider} durationMs {DurationMs} outcome {Outcome} failure {FailureType}", "generate_skill", "Groq", timer.ElapsedMilliseconds, "failure", "schema_validation");
                return Validation(errors);
            }
            loggerFactory.CreateLogger("AiOperations").LogInformation("AI operation {Operation} provider {Provider} durationMs {DurationMs} outcome {Outcome}", "generate_skill", "Groq", timer.ElapsedMilliseconds, "success");
            return Results.Ok(draft);
        }
        catch (AiProviderUnavailableException)
        {
            loggerFactory.CreateLogger("AiOperations").LogWarning("AI operation {Operation} provider {Provider} durationMs {DurationMs} outcome {Outcome} failure {FailureType}", "generate_skill", "Groq", timer.ElapsedMilliseconds, "failure", "provider_or_schema");
            return Results.Problem(statusCode: 503, title: "AI assistant unavailable", detail: "The AI service could not generate a draft.");
        }
    }

    private static async Task<IResult> UploadSkill(HttpRequest request, CancellationToken ct)
    {
        if (!HasSameOrigin(request)) return Results.Problem(statusCode: 403, title: "Upload origin could not be verified.");
        if (request.ContentLength is > MaxUploadBytes + 16 * 1024) return Results.Problem(statusCode: 413, title: "Skill file exceeds the 64 KB limit.");
        IFormCollection form;
        try { form = await request.ReadFormAsync(ct); }
        catch (InvalidDataException) { return Results.Problem(statusCode: 413, title: "Skill file exceeds the 64 KB limit or the form is invalid."); }
        if (form.Files.Count != 1 || form.Files[0].Length is 0 or > MaxUploadBytes) return Validation("file", "Upload exactly one non-empty file up to 64 KB.");
        var file = form.Files[0];
        try
        {
            await using var stream = file.OpenReadStream();
            using var memory = new MemoryStream();
            await stream.CopyToAsync(memory, ct);
            return Results.Ok(SkillUploadParser.Parse(file.FileName, memory.ToArray()));
        }
        catch (SkillUploadException ex) { return ex.StatusCode == 413 ? Results.Problem(statusCode: 413, title: ex.Message) : Validation("file", ex.Message); }
        catch (InvalidDataException) { return Results.Problem(statusCode: 413, title: "Skill file exceeds the 64 KB limit."); }
    }

    private static bool HasSameOrigin(HttpRequest request)
    {
        var source = request.Headers.Origin.FirstOrDefault() ?? request.Headers.Referer.FirstOrDefault();
        return Uri.TryCreate(source, UriKind.Absolute, out var uri) && uri.Host.Equals(request.Host.Host, StringComparison.OrdinalIgnoreCase) && uri.Scheme.Equals(request.Scheme, StringComparison.OrdinalIgnoreCase);
    }

    private static SkillDefinition ToDefinition(SkillWriteRequest request) => new(request.Name, request.Description, request.Instructions, request.Examples ?? [], 1);
    private static IResult Validation(string key, string message) => Results.ValidationProblem(new Dictionary<string, string[]> { [key] = [message] });
    private static IResult Validation(IEnumerable<string> errors) => Results.ValidationProblem(new Dictionary<string, string[]> { ["skill"] = errors.ToArray() });

    private static string ResolveTimeZone(string? timeZoneId)
    {
        if (string.IsNullOrWhiteSpace(timeZoneId) || timeZoneId.Length > 64 || !System.Text.RegularExpressions.Regex.IsMatch(timeZoneId, @"^[A-Za-z0-9_+/-]+$"))
            return "UTC";
        try { return TimeZoneInfo.FindSystemTimeZoneById(timeZoneId).Id; }
        catch (TimeZoneNotFoundException) { return "UTC"; }
        catch (InvalidTimeZoneException) { return "UTC"; }
    }
}
