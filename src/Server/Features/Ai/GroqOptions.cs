namespace RoomBooking.Server.Features.Ai;

public sealed class GroqOptions
{
    public const string SectionName = "Groq";
    public string ApiKey { get; set; } = string.Empty;
    public string Model { get; set; } = "llama-3.3-70b-versatile";
    public string Endpoint { get; set; } = "https://api.groq.com/openai/v1/chat/completions";
    public int TimeoutSeconds { get; set; } = 20;
}
