using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RoomBooking.Server.Infrastructure.Persistence;
using RoomBooking.Shared.Dtos.Auth;
using RoomBooking.Shared.Dtos.Resources;

namespace RoomBooking.IntegrationTests;

[Collection("IntegrationTests")]
public class AuthApiTests
{
    private readonly IntegrationTestWebFactory _factory;

    public AuthApiTests(IntegrationTestWebFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Register_ValidRequest_ReturnsOk()
    {
        var client = _factory.CreateClient();
        var request = new RegisterRequest(
            "newuser@test.com",
            "Password123!",
            "New User");

        var response = await client.PostAsJsonAsync("/api/auth/register", request);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var user = await response.Content.ReadFromJsonAsync<UserDto>();
        user.Should().NotBeNull();
        user!.Email.Should().Be(request.Email);
        user.DisplayName.Should().Be(request.DisplayName);
        user.Roles.Should().Contain("User");
    }

    [Fact]
    public async Task Register_DuplicateEmail_ReturnsValidationProblem()
    {
        var client = _factory.CreateClient();
        var request = new RegisterRequest("dup@test.com", "Password123!", "User 1");
        await client.PostAsJsonAsync("/api/auth/register", request);

        var dup = new RegisterRequest("dup@test.com", "Password456!", "User 2");
        var response = await client.PostAsJsonAsync("/api/auth/register", dup);
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Register_InvalidEmail_ReturnsValidationProblem()
    {
        var client = _factory.CreateClient();
        var request = new RegisterRequest("not-email", "Password123!", "User");
        var response = await client.PostAsJsonAsync("/api/auth/register", request);
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Login_ValidCredentials_ReturnsOk()
    {
        var client = _factory.CreateClient();
        await client.PostAsJsonAsync("/api/auth/register",
            new RegisterRequest("loginuser@test.com", "Password123!", "Login User"));

        var login = new LoginRequest("loginuser@test.com", "Password123!");
        var response = await client.PostAsJsonAsync("/api/auth/login", login);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Login_InvalidPassword_ReturnsUnauthorized()
    {
        var client = _factory.CreateClient();
        await client.PostAsJsonAsync("/api/auth/register",
            new RegisterRequest("badlogin@test.com", "Password123!", "Bad Login User"));

        var login = new LoginRequest("badlogin@test.com", "WrongPassword!");
        var response = await client.PostAsJsonAsync("/api/auth/login", login);
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}

[Collection("IntegrationTests")]
public class ResourcesApiTests
{
    private readonly IntegrationTestWebFactory _factory;

    public ResourcesApiTests(IntegrationTestWebFactory factory)
    {
        _factory = factory;
    }

    private async Task<HttpClient> CreateAdminClient()
    {
        var client = _factory.CreateClient();
        await client.PostAsJsonAsync("/api/auth/register",
            new RegisterRequest("resadmin@test.com", "Admin123!", "Res Admin"));

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var user = await db.Users.FirstAsync(u => u.Email == "resadmin@test.com");
        user.UserName.Should().NotBeNull();

        await client.PostAsJsonAsync("/api/auth/login",
            new LoginRequest("resadmin@test.com", "Admin123!"));
        return client;
    }

    [Fact]
    public async Task GetResources_Anonymous_ReturnsOk()
    {
        var client = _factory.CreateClient();
        var response = await client.GetAsync("/api/resources");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task CreateResource_Anonymous_ReturnsUnauthorized()
    {
        var client = _factory.CreateClient();
        var request = new CreateResourceRequest("Room 1", "Description");
        var response = await client.PostAsJsonAsync("/api/resources", request);
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task CreateResource_ValidData_SavesToDb()
    {
        var client = await CreateAdminClient();

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var adminUser = await db.Users.FirstAsync(u => u.Email == "resadmin@test.com");
            var role = await db.Roles.FirstAsync(r => r.Name == "Admin");
            db.UserRoles.Add(new Microsoft.AspNetCore.Identity.IdentityUserRole<string>
            {
                UserId = adminUser.Id,
                RoleId = role.Id
            });
            await db.SaveChangesAsync();
        }

        await client.PostAsJsonAsync("/api/auth/login",
            new LoginRequest("resadmin@test.com", "Admin123!"));

        var request = new CreateResourceRequest("Meeting Room Alpha", "10 seats, projector");
        var response = await client.PostAsJsonAsync("/api/resources", request);
        response.StatusCode.Should().Be(HttpStatusCode.Created);

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var resource = await db.Resources.FirstOrDefaultAsync(r => r.Name == "Meeting Room Alpha");
            resource.Should().NotBeNull();
            resource!.Description.Should().Be("10 seats, projector");
            resource.IsActive.Should().BeTrue();
        }
    }
}
