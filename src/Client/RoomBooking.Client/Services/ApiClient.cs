using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Components.WebAssembly.Http;

namespace RoomBooking.Client.Services;

public sealed class ApiClient(HttpClient http)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<T?> GetAsync<T>(string path, CancellationToken ct = default) =>
        await SendAsync<T>(HttpMethod.Get, path, null, ct);

    public async Task<T?> SendAsync<T>(HttpMethod method, string path, object? body = null, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(method, path);
        request.SetBrowserRequestCredentials(BrowserRequestCredentials.SameOrigin);
        if (body is HttpContent content) request.Content = content;
        else if (body is not null) request.Content = JsonContent.Create(body, options: JsonOptions);

        using var response = await http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
            throw new ApiException(response.StatusCode, await ReadProblemAsync(response, ct));
        if (response.StatusCode == HttpStatusCode.NoContent || response.Content.Headers.ContentLength == 0)
            return default;
        return await response.Content.ReadFromJsonAsync<T>(JsonOptions, ct);
    }

    private static async Task<string> ReadProblemAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var raw = await response.Content.ReadAsStringAsync(ct);
        if (string.IsNullOrWhiteSpace(raw)) return $"Request failed ({(int)response.StatusCode}).";
        try
        {
            using var document = JsonDocument.Parse(raw);
            var root = document.RootElement;
            if (root.TryGetProperty("detail", out var detail) && detail.ValueKind == JsonValueKind.String) return detail.GetString()!;
            if (root.TryGetProperty("title", out var title) && title.ValueKind == JsonValueKind.String) return title.GetString()!;
            if (root.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Object)
                return string.Join(" ", errors.EnumerateObject().SelectMany(entry => entry.Value.EnumerateArray()).Select(value => value.GetString()).Where(value => !string.IsNullOrWhiteSpace(value)));
        }
        catch (JsonException) { }
        return $"Request failed ({(int)response.StatusCode}).";
    }
}

public sealed class ApiException(HttpStatusCode statusCode, string message) : Exception(message)
{
    public HttpStatusCode StatusCode { get; } = statusCode;
    public bool IsConflict => StatusCode == HttpStatusCode.Conflict;
}
