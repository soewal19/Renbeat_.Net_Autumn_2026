using System.Text.RegularExpressions;

namespace RoomBooking.Server.Features.Ai;

/// <summary>Allows booking tools only when a message starts with an explicit booking instruction.</summary>
public static partial class BookingIntentDetector
{
    [GeneratedRegex(@"^(?:(?:please|kindly)\s+)?(?:book|reserve)\b|^(?:(?:can|could|would)\s+you\s+(?:please\s+)?)?(?:book|reserve)\b|^i\s+(?:want|need)\s+to\s+(?:book|reserve)\b|^i(?:'d| would)\s+like\s+to\s+(?:book|reserve)\b|^(?:пожалуйста\s+)?(?:забронируй(?:те)?|зарезервируй(?:те)?|забронировать|резервируй(?:те)?|закажи(?:те)?)\b|^я\s+хочу\s+(?:забронировать|зарезервировать)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ExplicitBookingPattern();

    public static bool IsExplicitBookingCommand(string message) =>
        ExplicitBookingPattern().IsMatch(message.Trim()) &&
        !Regex.IsMatch(message, @"\b(?:don't|do not|never)(?:\s+\w+){0,3}\s+(?:book|reserve)\b|не\s+(?:бронируй|резервируй|бронировать)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
}
