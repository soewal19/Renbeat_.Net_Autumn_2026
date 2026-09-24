using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using RoomBooking.Shared.Dtos.Auth;

namespace RoomBooking.IntegrationTests;

[Collection("IntegrationTests")]
public sealed class AiAuthorizationTests(IntegrationTestWebFactory factory)
{
    [Fact]
    public async Task SkillsRequireAuthenticationAndRegularUsersCannotManageThem()
    {
        var anonymous = factory.CreateClient();
        (await anonymous.GetAsync("/api/ai/skills")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        var user = factory.CreateClient();
        await user.PostAsJsonAsync("/api/auth/register", new RegisterRequest("skill-user@test.com", "Password123!", "Skill User"));
        await user.PostAsJsonAsync("/api/auth/login", new LoginRequest("skill-user@test.com", "Password123!"));
        var aiStatus = await user.GetFromJsonAsync<AiStatusResponse>("/api/ai/status");
        aiStatus!.Available.Should().BeFalse();
        (await user.PostAsJsonAsync("/api/ai/chat", new { message = "What is booked tomorrow?" })).StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        var response = await user.PostAsJsonAsync("/api/ai/skills", new
        {
            name = "Unauthorized skill", description = "Must be rejected", instructions = new[] { "Explain rules" }, examples = Array.Empty<string>()
        });
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    private sealed record AiStatusResponse(bool Available);
}
