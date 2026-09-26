using System.Security.Claims;
using FluentValidation;
using FluentValidation.Results;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.WebAssembly.Server;
using Azure.Monitor.OpenTelemetry.AspNetCore;
using OpenTelemetry.Logs;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.FileProviders;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.AspNetCore.RateLimiting;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.InMemory;
using RoomBooking.Server.Infrastructure.Identity;
using RoomBooking.Server.Infrastructure.Persistence;
using RoomBooking.Server.Infrastructure.Persistence.Entities;
using RoomBooking.Server.Infrastructure.SignalR.Hubs;
using RoomBooking.Server.Features.Ai;
using RoomBooking.Shared.Dtos.Auth;
using RoomBooking.Shared.Dtos.Bookings;
using RoomBooking.Shared.Dtos.Resources;
using RoomBooking.Shared.Dtos.Schedule;
using RoomBooking.Shared.SignalR;
using RoomBooking.Server.Components;
using LoginRequest = RoomBooking.Shared.Dtos.Auth.LoginRequest;
using RegisterRequest = RoomBooking.Shared.Dtos.Auth.RegisterRequest;

var builder = WebApplication.CreateBuilder(args);

// Keep local development logging on portable providers. The Windows Event Log provider can
// throw when the process has no Event Log permissions, masking handled API errors (for example,
// an unavailable optional AI provider) with a second exception while logging the first one.
builder.Logging.ClearProviders();
builder.Logging.AddConsole();
builder.Logging.AddDebug();

builder.Services.AddOpenApi(options =>
{
    options.AddDocumentTransformer((document, _, _) =>
    {
        document.Info = new Microsoft.OpenApi.OpenApiInfo
        {
            Title = "RoomBooking API",
            Version = "v1",
            Description = "Cookie-authenticated meeting-room booking API. Booking uniqueness is enforced by the database; a competing booking receives HTTP 409."
        };
        return Task.CompletedTask;
    });
});
builder.Services.AddRazorComponents().AddInteractiveWebAssemblyComponents();
if (!string.IsNullOrWhiteSpace(builder.Configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"]))
{
    builder.Services.AddOpenTelemetry().UseAzureMonitor();
    builder.Services.Configure<OpenTelemetryLoggerOptions>(options => options.IncludeScopes = true);
}

builder.Services.AddProblemDetails(options =>
{
    options.CustomizeProblemDetails = ctx =>
    {
        if (ctx.Exception is null) return;
        if (DbConcurrencyHelper.IsUniqueConstraintViolation(ctx.Exception))
        {
            ctx.ProblemDetails.Status = StatusCodes.Status409Conflict;
            ctx.ProblemDetails.Title = "Slot already booked";
            ctx.ProblemDetails.Type = "https://example.com/problems/slot-already-booked";
            ctx.ProblemDetails.Detail = "The selected time slot was booked by another user.";
            ctx.ProblemDetails.Extensions["code"] = "SLOT_ALREADY_BOOKED";
            ctx.HttpContext.Response.StatusCode = StatusCodes.Status409Conflict;
        }
    };
});
builder.Services.Configure<FormOptions>(options =>
{
    // Allow multipart headers/boundaries in addition to a 10 MB image; the endpoint enforces the file limit.
    options.MultipartBodyLengthLimit = 11 * 1024 * 1024;
    options.ValueCountLimit = 4;
});

builder.Services.AddDbContext<AppDbContext>(options =>
{
    var connStr = builder.Configuration.GetConnectionString("DefaultConnection");
    if (!string.IsNullOrWhiteSpace(connStr))
    {
        options.UseSqlServer(connStr, o =>
            o.EnableRetryOnFailure(maxRetryCount: 5, maxRetryDelay: TimeSpan.FromSeconds(5), errorNumbersToAdd: null));
    }
    else
    {
        options.UseInMemoryDatabase("RoomBooking_InMemory");
    }
});

builder.Services.AddIdentityCore<ApplicationUser>(options =>
    {
        options.User.RequireUniqueEmail = true;
        options.Password.RequireDigit = true;
        options.Password.RequireUppercase = true;
        options.Password.RequireLowercase = true;
        options.Password.RequireNonAlphanumeric = false;
        options.Password.RequiredLength = 6;
    })
    .AddRoles<IdentityRole>()
    .AddEntityFrameworkStores<AppDbContext>()
    .AddSignInManager()
    .AddDefaultTokenProviders();

builder.Services.AddAuthentication(IdentityConstants.ApplicationScheme)
    .AddIdentityCookies(options => { });
builder.Services.ConfigureApplicationCookie(options =>
{
    options.Events.OnRedirectToLogin = context =>
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        return Task.CompletedTask;
    };
    options.Events.OnRedirectToAccessDenied = context =>
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        return Task.CompletedTask;
    };
});

builder.Services.AddAuthorizationBuilder()
    .AddPolicy(AppRoles.Admin, policy => policy.RequireRole(AppRoles.Admin));
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.OnRejected = async (context, cancellationToken) =>
    {
        context.HttpContext.Response.ContentType = "application/problem+json";
        await context.HttpContext.Response.WriteAsJsonAsync(new ProblemDetails
        {
            Status = StatusCodes.Status429TooManyRequests,
            Title = "AI request limit reached",
            Detail = "Wait a minute before sending another AI request."
        }, cancellationToken);
    };
    options.AddPolicy("ai", context => RateLimitPartition.GetFixedWindowLimiter(
        context.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 15, Window = TimeSpan.FromMinutes(1), QueueLimit = 0, AutoReplenishment = true }));
});

var dpKeysDir = builder.Environment.IsDevelopment()
    ? Path.Combine(builder.Environment.ContentRootPath, "bin", "dataprotection-keys")
    : Path.Combine(Environment.GetEnvironmentVariable("HOME") ?? AppContext.BaseDirectory, ".aspnet", "DataProtection-Keys");
Directory.CreateDirectory(dpKeysDir);
builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(dpKeysDir))
    .SetApplicationName("RoomBooking.Server");

var signalrConnStr = builder.Configuration.GetConnectionString("AzureSignalR");
var signalrBuilder = builder.Services.AddSignalR();
if (!string.IsNullOrWhiteSpace(signalrConnStr))
{
    signalrBuilder.AddAzureSignalR(signalrConnStr);
}

builder.Services.AddValidatorsFromAssemblyContaining(typeof(Program));
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ICurrentUserService, CurrentUserService>();
builder.Services.AddScoped<IAiToolService, AiToolService>();
builder.Services.AddScoped<IAiSkillService, AiSkillService>();
builder.Services.AddOptions<GroqOptions>().Configure(options =>
{
    options.ApiKey = builder.Configuration["GROQ_API_KEY"] ?? builder.Configuration["Groq:ApiKey"] ?? string.Empty;
    options.Model = builder.Configuration["GROQ_MODEL"] ?? builder.Configuration["Groq:Model"] ?? options.Model;
    options.FallbackModel = builder.Configuration["GROQ_FALLBACK_MODEL"] ?? builder.Configuration["Groq:FallbackModel"] ?? options.FallbackModel;
    options.Endpoint = builder.Configuration["GROQ_ENDPOINT"] ?? builder.Configuration["Groq:Endpoint"] ?? options.Endpoint;
    if (int.TryParse(builder.Configuration["GROQ_TIMEOUT_SECONDS"] ?? builder.Configuration["Groq:TimeoutSeconds"], out var timeout))
        options.TimeoutSeconds = Math.Clamp(timeout, 1, 120);
});
builder.Services.AddHttpClient<IAiAssistant, GroqAiAssistant>((services, http) =>
{
    var options = services.GetRequiredService<Microsoft.Extensions.Options.IOptions<GroqOptions>>().Value;
    http.Timeout = TimeSpan.FromSeconds(Math.Clamp(options.TimeoutSeconds, 1, 120));
});

var app = builder.Build();

app.UseExceptionHandler();
app.UseStatusCodePages();

if (app.Environment.IsDevelopment())
{
    app.UseWebAssemblyDebugging();
    app.MapOpenApi();
    app.UseSwaggerUI(c =>
    {
        c.SwaggerEndpoint("/openapi/v1.json", "RoomBooking API v1");
        c.DisplayRequestDuration();
    });
}

if (!app.Environment.IsDevelopment())
{
    app.UseHttpsRedirection();
}
app.UseStaticFiles();
var architectureDocsPath = Path.Combine(app.Environment.ContentRootPath, "docs", "architecture");
if (!Directory.Exists(architectureDocsPath))
    architectureDocsPath = Path.GetFullPath(Path.Combine(app.Environment.ContentRootPath, "..", "..", "docs", "architecture"));
var docsContentTypes = new FileExtensionContentTypeProvider();
docsContentTypes.Mappings[".md"] = "text/markdown; charset=utf-8";
if (Directory.Exists(architectureDocsPath))
{
    app.UseStaticFiles(new StaticFileOptions
    {
        FileProvider = new PhysicalFileProvider(architectureDocsPath),
        RequestPath = "/docs/architecture",
        ContentTypeProvider = docsContentTypes
    });
}
app.UseRouting();
app.UseAuthentication();
app.UseRateLimiter();
app.UseAuthorization();
app.UseAntiforgery();

app.MapHub<ScheduleHub>("/hubs/schedule")
   .RequireAuthorization();

app.MapGroup("/api/auth")
   .WithTags("Auth")
   .WithDescription("Identity registration and cookie session endpoints.")
   .MapAuthApi();

app.MapGroup("/api/resources")
   .WithTags("Resources")
   .MapResourcesApi();

app.MapGroup("/api/resources")
   .WithTags("Schedule")
   .MapScheduleApi();

app.MapGroup("/api/bookings")
   .WithTags("Bookings")
   .MapBookingsApi();

app.MapGroup("/api/admin")
   .WithTags("Admin")
   .WithDescription("All endpoints require the Admin role.")
   .RequireAuthorization(AppRoles.Admin)
   .MapAdminApi();

app.MapGroup("/api/directory")
   .WithTags("Directory")
   .RequireAuthorization()
   .MapDirectoryApi();

app.MapGroup("/api")
   .MapAiApi();

app.MapStaticAssets();
var documentationRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "docs"));
app.MapGet("/docs/{**documentPath}", (string? documentPath) =>
{
    if (string.IsNullOrWhiteSpace(documentPath)) return Results.NotFound();
    var filePath = Path.GetFullPath(Path.Combine(documentationRoot, documentPath));
    var docsPrefix = documentationRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
    if (!filePath.StartsWith(docsPrefix, StringComparison.OrdinalIgnoreCase) || !File.Exists(filePath)) return Results.NotFound();
    return Results.File(filePath, "text/markdown; charset=utf-8");
}).AllowAnonymous();
app.MapRazorComponents<App>()
   .AddInteractiveWebAssemblyRenderMode()
   .AddAdditionalAssemblies(typeof(RoomBooking.Client.ClientMarker).Assembly);

await StartupTasks.ApplyMigrationsAndSeedAsync(app);

app.Run();

public static class DbConcurrencyHelper
{
    public static bool IsUniqueConstraintViolation(Exception? ex)
    {
        while (ex != null)
        {
            if (ex is Microsoft.EntityFrameworkCore.DbUpdateException updEx)
            {
                if (updEx.InnerException is SqlException sqlEx)
                {
                    if (sqlEx.Number is 2601 or 2627)
                    {
                        var msg = sqlEx.Message;
                        if (msg.Contains("IX_Bookings_TimeSlotId_Unique", StringComparison.OrdinalIgnoreCase) ||
                            msg.Contains("TimeSlotId", StringComparison.OrdinalIgnoreCase))
                            return true;
                    }
                }
            }
            ex = ex.InnerException;
        }
        return false;
    }
}

public static class StartupTasks
{
    public static async Task ApplyMigrationsAndSeedAsync(WebApplication app)
    {
        using var scope = app.Services.CreateScope();
        var services = scope.ServiceProvider;
        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger("Startup");

        try
        {
            var db = services.GetRequiredService<AppDbContext>();
            if (db.Database.IsSqlServer())
            {
                await db.Database.MigrateAsync();
                logger.LogInformation("Database migrations applied.");
            }
            else
            {
                await db.Database.EnsureCreatedAsync();
                logger.LogInformation("Database ensured.");
            }

            var roleManager = services.GetRequiredService<RoleManager<IdentityRole>>();
            var userManager = services.GetRequiredService<UserManager<ApplicationUser>>();
            var configuration = services.GetRequiredService<IConfiguration>();
            await RoleSeeder.SeedAsync(roleManager, userManager, configuration, logger);
            if (configuration.GetValue<bool>("Seed:DemoData"))
            {
                var environment = services.GetRequiredService<IWebHostEnvironment>();
                await DemoDataSeeder.SeedAsync(db, userManager, roleManager, environment, logger);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "An error occurred during migration / seeding.");
            throw;
        }
    }
}

public interface ICurrentUserService
{
    string? UserId { get; }
    Task<ApplicationUser?> GetUserAsync(CancellationToken ct);
}

public sealed class CurrentUserService : ICurrentUserService
{
    private readonly IHttpContextAccessor _http;
    private readonly UserManager<ApplicationUser> _userManager;

    public CurrentUserService(IHttpContextAccessor http, UserManager<ApplicationUser> userManager)
    {
        _http = http;
        _userManager = userManager;
    }

    public string? UserId => _http.HttpContext?.User.FindFirstValue(ClaimTypes.NameIdentifier);

    public Task<ApplicationUser?> GetUserAsync(CancellationToken ct)
    {
        if (UserId is null) return Task.FromResult<ApplicationUser?>(null);
        return _userManager.FindByIdAsync(UserId)!;
    }
}

public static class EndpointMappings
{
    public static RouteGroupBuilder MapDirectoryApi(this RouteGroupBuilder group)
    {
        group.MapPost("/search", async (DirectorySearchRequest request, AppDbContext db, CancellationToken ct) =>
        {
            var term = request.Query?.Trim();
            if (term?.Length > 100) term = term[..100];
            var users = db.Users.AsNoTracking();
            var rooms = db.Resources.AsNoTracking();
            if (!string.IsNullOrWhiteSpace(term))
            {
                users = users.Where(user => user.DisplayName.Contains(term) || (user.Email != null && user.Email.Contains(term)));
                rooms = rooms.Where(room => room.Name.Contains(term) || room.Description.Contains(term));
            }

            var resultUsers = await users.OrderBy(user => user.DisplayName).Take(100)
                .Select(user => new { user.Id, user.DisplayName, user.Email })
                .ToListAsync(ct);
            var resultRooms = await rooms.OrderBy(room => room.Name).Take(100)
                .Select(room => new { room.Id, room.Name, room.Description, room.IsActive, HasImage = room.ImageData != null })
                .ToListAsync(ct);
            return TypedResults.Ok(new
            {
                users = resultUsers,
                rooms = resultRooms.Select(room => new
                {
                    room.Id, room.Name, room.Description, room.IsActive,
                    ImageUrl = GetResourceImageUrl(room.Id, room.HasImage)
                })
            });
        }).WithName("SearchDirectory")
          .WithSummary("Search database users and rooms")
          .WithDescription("Authentication required. Search is limited to 100 characters and returns at most 100 results per entity type.")
          .Produces(StatusCodes.Status200OK)
          .ProducesProblem(StatusCodes.Status401Unauthorized);
        return group;
    }

    public static RouteGroupBuilder MapAuthApi(this RouteGroupBuilder group)
    {
        group.MapPost("/register", async Task<Results<Ok<UserDto>, ValidationProblem>> (
            RegisterRequest req,
            UserManager<ApplicationUser> userManager,
            RoleManager<IdentityRole> roleManager,
            IValidator<RegisterRequest> validator,
            CancellationToken ct) =>
        {
            var vr = await validator.ValidateAsync(req, ct);
            if (!vr.IsValid) return TypedResults.ValidationProblem(vr.ToModelStateDict());

            var user = new ApplicationUser
            {
                UserName = req.Email,
                Email = req.Email,
                DisplayName = req.DisplayName,
                EmailConfirmed = true,
                CreatedAtUtc = DateTimeOffset.UtcNow
            };

            var result = await userManager.CreateAsync(user, req.Password);
            if (!result.Succeeded)
                return TypedResults.ValidationProblem(result.ToModelStateDict());

            if (!await roleManager.RoleExistsAsync(AppRoles.User))
                await roleManager.CreateAsync(new IdentityRole(AppRoles.User));

            await userManager.AddToRoleAsync(user, AppRoles.User);

            var roles = await userManager.GetRolesAsync(user);
            return TypedResults.Ok(ToUserDto(user, roles));
        })
        .WithName("Register")
        .WithSummary("Register a regular user")
        .Produces<UserDto>(StatusCodes.Status200OK)
        .ProducesValidationProblem(StatusCodes.Status400BadRequest);

        group.MapPost("/login", async Task<Results<Ok<UserDto>, UnauthorizedHttpResult, ValidationProblem>> (
            LoginRequest req,
            SignInManager<ApplicationUser> signIn,
            UserManager<ApplicationUser> userManager,
            IValidator<LoginRequest> validator,
            CancellationToken ct) =>
        {
            var vr = await validator.ValidateAsync(req, ct);
            if (!vr.IsValid) return TypedResults.ValidationProblem(vr.ToModelStateDict());

            var user = await userManager.FindByEmailAsync(req.Email);
            if (user is null) return TypedResults.Unauthorized();

            var result = await signIn.PasswordSignInAsync(user, req.Password, isPersistent: false, lockoutOnFailure: false);
            if (!result.Succeeded) return TypedResults.Unauthorized();

            var roles = await userManager.GetRolesAsync(user);
            return TypedResults.Ok(ToUserDto(user, roles));
        })
        .WithName("Login")
        .WithSummary("Sign in and create an authentication cookie")
        .Produces<UserDto>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status401Unauthorized)
        .ProducesValidationProblem(StatusCodes.Status400BadRequest);

        group.MapPost("/logout", async (SignInManager<ApplicationUser> signIn) =>
        {
            await signIn.SignOutAsync();
            return TypedResults.Ok();
        })
        .WithName("Logout");

        group.MapGet("/me", [Authorize] async Task<Results<Ok<UserDto>, UnauthorizedHttpResult>> (
            UserManager<ApplicationUser> userManager,
            ICurrentUserService currentUser,
            CancellationToken ct) =>
        {
            var user = await currentUser.GetUserAsync(ct);
            if (user is null) return TypedResults.Unauthorized();

            var roles = await userManager.GetRolesAsync(user);
            return TypedResults.Ok(ToUserDto(user, roles));
        })
        .WithName("Me");

        group.MapPut("/profile", [Authorize] async (UpdateProfileRequest request, UserManager<ApplicationUser> userManager, ICurrentUserService currentUser, CancellationToken ct) =>
        {
            var user = await currentUser.GetUserAsync(ct);
            if (user is null) return Results.Unauthorized();
            var name = request.DisplayName?.Trim();
            if (string.IsNullOrWhiteSpace(name) || name.Length > 200)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["displayName"] = ["Name is required and must be at most 200 characters."] });
            if (request.PhoneNumber?.Length > 32)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["phoneNumber"] = ["Phone number must be at most 32 characters."] });
            user.DisplayName = name;
            user.PhoneNumber = string.IsNullOrWhiteSpace(request.PhoneNumber) ? null : request.PhoneNumber.Trim();
            var result = await userManager.UpdateAsync(user);
            return result.Succeeded ? Results.Ok(ToUserDto(user, await userManager.GetRolesAsync(user))) : Results.ValidationProblem(result.ToModelStateDict());
        }).WithName("UpdateMyProfile");

        group.MapPost("/me/avatar", [Authorize] async (IFormFile file, HttpContext http, UserManager<ApplicationUser> userManager, ICurrentUserService currentUser, CancellationToken ct) =>
        {
            if (!IsSameOriginRequest(http)) return Results.Forbid();
            var user = await currentUser.GetUserAsync(ct);
            if (user is null) return Results.Unauthorized();
            if (file.Length is <= 0 or > 10 * 1024 * 1024) return Results.Problem("Avatar must be between 1 byte and 10 MB.", statusCode: 413);
            var data = new byte[checked((int)file.Length)];
            await using (var stream = file.OpenReadStream()) await stream.ReadExactlyAsync(data, ct);
            var contentType = GetUploadedImageContentType(data);
            if (contentType is null) return Results.ValidationProblem(new Dictionary<string, string[]> { ["file"] = ["Upload a valid JPEG, PNG, or WebP image."] });
            user.AvatarData = data;
            user.AvatarContentType = contentType;
            var result = await userManager.UpdateAsync(user);
            return result.Succeeded ? Results.Ok(ToUserDto(user, await userManager.GetRolesAsync(user))) : Results.ValidationProblem(result.ToModelStateDict());
        }).DisableAntiforgery().WithName("UploadMyAvatar");

        group.MapGet("/me/avatar", [Authorize] async (UserManager<ApplicationUser> userManager, ICurrentUserService currentUser) =>
        {
            var user = await currentUser.GetUserAsync(CancellationToken.None);
            return user?.AvatarData is { Length: > 0 } data && user.AvatarContentType is { } type
                ? Results.File(data, type)
                : Results.NotFound();
        }).WithName("GetMyAvatar");

        group.MapDelete("/me/avatar", [Authorize] async (UserManager<ApplicationUser> userManager, ICurrentUserService currentUser, CancellationToken ct) =>
        {
            var user = await currentUser.GetUserAsync(ct);
            if (user is null) return Results.Unauthorized();
            user.AvatarData = null; user.AvatarContentType = null;
            var result = await userManager.UpdateAsync(user);
            return result.Succeeded ? Results.NoContent() : Results.ValidationProblem(result.ToModelStateDict());
        }).WithName("DeleteMyAvatar");

        group.MapPost("/password", [Authorize] async (ChangePasswordRequest request, UserManager<ApplicationUser> userManager, ICurrentUserService currentUser, CancellationToken ct) =>
        {
            var user = await currentUser.GetUserAsync(ct);
            if (user is null) return Results.Unauthorized();
            var result = await userManager.ChangePasswordAsync(user, request.CurrentPassword ?? string.Empty, request.NewPassword ?? string.Empty);
            return result.Succeeded ? Results.NoContent() : Results.ValidationProblem(result.ToModelStateDict());
        }).WithName("ChangeMyPassword");

        return group;
    }

    public static RouteGroupBuilder MapResourcesApi(this RouteGroupBuilder group)
    {
        group.MapGet("/", async (AppDbContext db, CancellationToken ct) =>
        {
            var resources = await db.Resources
                .OrderBy(r => r.Name)
                .Select(r => new { r.Id, r.Name, r.Description, r.IsActive, r.CreatedAtUtc, r.UpdatedAtUtc, HasImage = r.ImageData != null })
                .ToListAsync(ct);
            return TypedResults.Ok(resources.Select(r => new ResourceDto(r.Id, r.Name, r.Description, r.IsActive,
                r.CreatedAtUtc, r.UpdatedAtUtc, GetResourceImageUrl(r.Id, r.HasImage))).ToList());
        })
        .WithName("GetResources")
        .WithSummary("List meeting rooms")
        .Produces<List<ResourceDto>>(StatusCodes.Status200OK);

        group.MapGet("/{id:int}", async Task<Results<Ok<ResourceDto>, NotFound>> (
            int id, AppDbContext db, CancellationToken ct) =>
        {
            var r = await db.Resources.FindAsync([id], ct);
            if (r is null) return TypedResults.NotFound();
            return TypedResults.Ok(new ResourceDto(r.Id, r.Name, r.Description, r.IsActive, r.CreatedAtUtc, r.UpdatedAtUtc,
                GetResourceImageUrl(r.Id, r.ImageData is { Length: > 0 })));
        })
        .WithName("GetResourceById")
        .WithSummary("Get a room by ID")
        .Produces<ResourceDto>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status404NotFound);

        group.MapPost("/", [Authorize(AppRoles.Admin)] async Task<Results<Created<ResourceDto>, ValidationProblem>> (
            CreateResourceRequest req,
            AppDbContext db,
            IValidator<CreateResourceRequest> validator,
            CancellationToken ct) =>
        {
            var vr = await validator.ValidateAsync(req, ct);
            if (!vr.IsValid) return TypedResults.ValidationProblem(vr.ToModelStateDict());

            var now = DateTimeOffset.UtcNow;
            var resource = new Resource
            {
                Name = req.Name.Trim(),
                Description = req.Description?.Trim() ?? string.Empty,
                IsActive = true,
                CreatedAtUtc = now,
                UpdatedAtUtc = now
            };
            db.Resources.Add(resource);
            await db.SaveChangesAsync(ct);

            var dto = new ResourceDto(resource.Id, resource.Name, resource.Description, resource.IsActive, resource.CreatedAtUtc,
                resource.UpdatedAtUtc, GetResourceImageUrl(resource.Id, hasImage: false));
            return TypedResults.Created($"/api/resources/{resource.Id}", dto);
        })
        .WithName("CreateResource")
        .WithSummary("Create a meeting room (Admin)")
        .Produces<ResourceDto>(StatusCodes.Status201Created)
        .ProducesValidationProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status401Unauthorized)
        .ProducesProblem(StatusCodes.Status403Forbidden);

        group.MapPut("/{id:int}", [Authorize(AppRoles.Admin)] async Task<Results<NoContent, NotFound, ValidationProblem>> (
            int id,
            UpdateResourceRequest req,
            AppDbContext db,
            IValidator<UpdateResourceRequest> validator,
            CancellationToken ct) =>
        {
            var vr = await validator.ValidateAsync(req, ct);
            if (!vr.IsValid) return TypedResults.ValidationProblem(vr.ToModelStateDict());

            var r = await db.Resources.FindAsync([id], ct);
            if (r is null) return TypedResults.NotFound();

            r.Name = req.Name.Trim();
            r.Description = req.Description?.Trim() ?? string.Empty;
            r.IsActive = req.IsActive;
            r.UpdatedAtUtc = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
            return TypedResults.NoContent();
        })
        .WithName("UpdateResource")
        .WithSummary("Update a meeting room (Admin)")
        .Produces(StatusCodes.Status204NoContent)
        .ProducesValidationProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status401Unauthorized)
        .ProducesProblem(StatusCodes.Status403Forbidden)
        .Produces(StatusCodes.Status404NotFound);

        group.MapDelete("/{id:int}", [Authorize(AppRoles.Admin)] async Task<Results<NoContent, NotFound>> (
            int id, AppDbContext db, CancellationToken ct) =>
        {
            var r = await db.Resources.FindAsync([id], ct);
            if (r is null) return TypedResults.NotFound();
            db.Resources.Remove(r);
            await db.SaveChangesAsync(ct);
            return TypedResults.NoContent();
        })
        .WithName("DeleteResource")
        .WithSummary("Delete a meeting room (Admin)")
        .Produces(StatusCodes.Status204NoContent)
        .ProducesProblem(StatusCodes.Status401Unauthorized)
        .ProducesProblem(StatusCodes.Status403Forbidden)
        .Produces(StatusCodes.Status404NotFound);

        group.MapGet("/{id:int}/image", async Task<Results<FileContentHttpResult, NotFound>> (
            int id, AppDbContext db, CancellationToken ct) =>
        {
            var image = await db.Resources.AsNoTracking()
                .Where(resource => resource.Id == id)
                .Select(resource => new { resource.ImageData, resource.ImageContentType })
                .FirstOrDefaultAsync(ct);
            if (image?.ImageData is not { Length: > 0 } || image.ImageContentType is null)
                return TypedResults.NotFound();
            return TypedResults.File(image.ImageData, image.ImageContentType);
        })
        .WithName("GetResourceImage")
        .WithSummary("Get an uploaded room image")
        .Produces(StatusCodes.Status404NotFound);

        group.MapPost("/{id:int}/image", [Authorize(AppRoles.Admin)] async (
            int id, IFormFile file, HttpContext http, AppDbContext db, CancellationToken ct) =>
        {
            if (!IsSameOriginRequest(http)) return Results.Forbid();
            if (file.Length is <= 0 or > 10 * 1024 * 1024)
                return Results.Problem("Room image must be between 1 byte and 10 MB.", statusCode: 413);

            var data = new byte[checked((int)file.Length)];
            await using (var stream = file.OpenReadStream()) await stream.ReadExactlyAsync(data, ct);
            var contentType = GetUploadedImageContentType(data);
            if (contentType is null)
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["file"] = ["Upload a valid JPEG, PNG, or WebP image."]
                });

            var resource = await db.Resources.FirstOrDefaultAsync(resource => resource.Id == id, ct);
            if (resource is null) return Results.NotFound();
            resource.ImageData = data;
            resource.ImageContentType = contentType;
            resource.UpdatedAtUtc = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        })
        .DisableAntiforgery()
        .WithName("UploadResourceImage")
        .WithSummary("Upload or replace a room image (Admin)")
        .Produces(StatusCodes.Status204NoContent)
        .ProducesValidationProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status401Unauthorized)
        .ProducesProblem(StatusCodes.Status403Forbidden)
        .ProducesProblem(StatusCodes.Status413PayloadTooLarge)
        .Produces(StatusCodes.Status404NotFound);

        return group;
    }

    public static RouteGroupBuilder MapScheduleApi(this RouteGroupBuilder group)
    {
        group.MapGet("/{id:int}/schedule", async Task<Results<Ok<List<TimeSlotDto>>, NotFound>> (
            int id,
            [FromQuery] DateOnly? date,
            AppDbContext db,
            CancellationToken ct) =>
        {
            var resource = await db.Resources.FindAsync([id], ct);
            if (resource is null) return TypedResults.NotFound();

            var query = db.TimeSlots.Where(ts => ts.ResourceId == id);

            if (date.HasValue)
            {
                var dayStart = date.Value.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
                var dayEnd = dayStart.AddDays(1);
                query = query.Where(ts => ts.StartUtc >= new DateTimeOffset(dayStart) && ts.StartUtc < new DateTimeOffset(dayEnd));
            }

            var slots = await query
                .OrderBy(ts => ts.StartUtc)
                .Select(ts => new TimeSlotDto(
                    ts.Id,
                    ts.ResourceId,
                    ts.StartUtc,
                    ts.EndUtc,
                    ts.Booking != null,
                    ts.Booking == null ? null : new BookingInfoDto(
                        ts.Booking.Id,
                        ts.Booking.CreatedAtUtc)))
                .ToListAsync(ct);

            return TypedResults.Ok(slots);
        })
        .WithName("GetResourceSchedule")
        .WithSummary("View a room schedule and slot availability")
        .WithDescription("Optionally filter by a UTC calendar date using YYYY-MM-DD.")
        .Produces<List<TimeSlotDto>>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status404NotFound);

        group.MapPost("/{id:int}/slots", [Authorize(AppRoles.Admin)] async Task<Results<Created<List<TimeSlotDto>>, NotFound, ValidationProblem>> (
            int id,
            List<CreateTimeSlotRequest> slots,
            AppDbContext db,
            IValidator<CreateTimeSlotRequest> validator,
            CancellationToken ct) =>
        {
            var resource = await db.Resources.FindAsync([id], ct);
            if (resource is null) return TypedResults.NotFound();

            if (slots.Count == 0)
                return TypedResults.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["slots"] = ["At least one time slot is required"]
                });

            var allErrors = new Dictionary<string, string[]>(StringComparer.Ordinal);
            for (int i = 0; i < slots.Count; i++)
            {
                var vr = await validator.ValidateAsync(slots[i], ct);
                if (!vr.IsValid)
                {
                    foreach (var f in vr.Errors)
                    {
                        var key = $"slots[{i}].{f.PropertyName}";
                        if (!allErrors.ContainsKey(key)) allErrors[key] = [];
                        allErrors[key] = [.. allErrors[key], f.ErrorMessage];
                    }
                }
            }
            if (allErrors.Count > 0) return TypedResults.ValidationProblem(allErrors);

            var toAdd = new List<TimeSlot>();
            foreach (var s in slots)
            {
                toAdd.Add(new TimeSlot
                {
                    ResourceId = id,
                    StartUtc = s.StartUtc,
                    EndUtc = s.EndUtc
                });
            }
            db.TimeSlots.AddRange(toAdd);
            await db.SaveChangesAsync(ct);

            var dtos = toAdd.Select(ts => new TimeSlotDto(ts.Id, ts.ResourceId, ts.StartUtc, ts.EndUtc, false, null)).ToList();
            return TypedResults.Created($"/api/resources/{id}/schedule", dtos);
        })
        .WithName("CreateTimeSlots")
        .WithSummary("Add fixed UTC time slots to a room (Admin)")
        .Produces<List<TimeSlotDto>>(StatusCodes.Status201Created)
        .ProducesValidationProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status401Unauthorized)
        .ProducesProblem(StatusCodes.Status403Forbidden)
        .Produces(StatusCodes.Status404NotFound);

        return group;
    }

    public static RouteGroupBuilder MapBookingsApi(this RouteGroupBuilder group)
    {
        group.MapPost("/", [Authorize] async Task<Results<Created<BookingDto>, ValidationProblem, ProblemHttpResult>> (
            CreateBookingRequest req,
            AppDbContext db,
            ICurrentUserService currentUser,
            IHubContext<ScheduleHub> hub,
            ILogger<Program> logger,
            IValidator<CreateBookingRequest> validator,
            CancellationToken ct) =>
        {
            var vr = await validator.ValidateAsync(req, ct);
            if (!vr.IsValid) return TypedResults.ValidationProblem(vr.ToModelStateDict());

            var userId = currentUser.UserId;
            if (string.IsNullOrWhiteSpace(userId)) return TypedResults.Problem(statusCode: 401);

            var slot = await db.TimeSlots
                .Include(ts => ts.Resource)
                .FirstOrDefaultAsync(ts => ts.Id == req.TimeSlotId, ct);

            if (slot is null)
                return TypedResults.Problem(statusCode: 404, title: "Time slot not found");

            var booking = new Booking
            {
                TimeSlotId = req.TimeSlotId,
                UserId = userId,
                CreatedAtUtc = DateTimeOffset.UtcNow,
                IsAiGenerated = false
            };
            db.Bookings.Add(booking);

            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (Microsoft.EntityFrameworkCore.DbUpdateException ex) when (DbConcurrencyHelper.IsUniqueConstraintViolation(ex))
            {
                logger.LogInformation("Booking conflict for slot {SlotId}", req.TimeSlotId);
                return TypedResults.Problem(new ProblemDetails
                {
                    Status = 409,
                    Title = "Slot already booked",
                    Type = "https://example.com/problems/slot-already-booked",
                    Detail = "The selected time slot was booked by another user.",
                    Extensions = { ["code"] = "SLOT_ALREADY_BOOKED" }
                });
            }

            var created = await db.Bookings
                .AsNoTracking()
                .Include(b => b.TimeSlot).ThenInclude(ts => ts.Resource)
                .Include(b => b.User)
                .FirstAsync(b => b.Id == booking.Id, ct);

            var dto = new BookingDto(
                created.Id,
                created.TimeSlotId,
                created.TimeSlot.StartUtc,
                created.TimeSlot.EndUtc,
                created.TimeSlot.ResourceId,
                created.TimeSlot.Resource.Name,
                created.UserId,
                created.User.Email ?? string.Empty,
                created.CreatedAtUtc,
                created.IsAiGenerated);

            var evt = new SlotBookedEvent(
                created.TimeSlotId,
                created.TimeSlot.ResourceId,
                created.Id,
                created.CreatedAtUtc);

            var grp = ScheduleHub.GetGroupName(created.TimeSlot.ResourceId);
            await hub.Clients.Group(grp).SendAsync(ScheduleHubMethods.SlotBooked, evt, ct);

            logger.LogInformation("Booking {BookingId} created for slot {SlotId} by user {UserId}",
                created.Id, created.TimeSlotId, created.UserId);

            return TypedResults.Created($"/api/bookings/{created.Id}", dto);
        })
        .WithName("CreateBooking")
        .WithSummary("Book one available time slot")
        .WithDescription("An authenticated user attempts an insert protected by the database unique index on Booking.TimeSlotId. Exactly one concurrent request can succeed; competitors receive 409 Conflict.")
        .Produces<BookingDto>(StatusCodes.Status201Created)
        .ProducesValidationProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status401Unauthorized)
        .ProducesProblem(StatusCodes.Status403Forbidden)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .ProducesProblem(StatusCodes.Status409Conflict);

        group.MapGet("/me", [Authorize] async (
            AppDbContext db,
            ICurrentUserService currentUser,
            CancellationToken ct) =>
        {
            var userId = currentUser.UserId ?? string.Empty;
            var bookings = await db.Bookings
                .AsNoTracking()
                .Where(b => b.UserId == userId)
                .Include(b => b.TimeSlot).ThenInclude(ts => ts.Resource)
                .Include(b => b.User)
                .OrderByDescending(b => b.CreatedAtUtc)
                .Select(b => new BookingDto(
                    b.Id,
                    b.TimeSlotId,
                    b.TimeSlot.StartUtc,
                    b.TimeSlot.EndUtc,
                    b.TimeSlot.ResourceId,
                    b.TimeSlot.Resource.Name,
                    b.UserId,
                    b.User.Email ?? string.Empty,
                    b.CreatedAtUtc,
                    b.IsAiGenerated))
                .ToListAsync(ct);

            return TypedResults.Ok(bookings);
        })
        .WithName("GetMyBookings")
        .WithSummary("List the authenticated user's bookings")
        .Produces<List<BookingDto>>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status401Unauthorized)
        .ProducesProblem(StatusCodes.Status403Forbidden);

        group.MapDelete("/{id:int}", [Authorize] async (
            int id,
            AppDbContext db,
            ICurrentUserService currentUser,
            IHubContext<ScheduleHub> hub,
            ILogger<Program> logger,
            CancellationToken ct) =>
        {
            var userId = currentUser.UserId;
            if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();
            var booking = await db.Bookings.Include(x => x.TimeSlot)
                .FirstOrDefaultAsync(x => x.Id == id && x.UserId == userId && x.TimeSlot.StartUtc > DateTimeOffset.UtcNow, ct);
            if (booking is null) return Results.NotFound();

            db.Bookings.Remove(booking);
            await db.SaveChangesAsync(ct);
            var evt = new SlotCancelledEvent(booking.TimeSlotId, booking.TimeSlot.ResourceId, booking.Id);
            try { await hub.Clients.Group(ScheduleHub.GetGroupName(evt.ResourceId)).SendAsync(ScheduleHubMethods.SlotCancelled, evt, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning("Could not publish realtime update for cancelled booking {BookingId}", booking.Id);
            }
            logger.LogInformation("Booking {BookingId} cancelled by user {UserId}", booking.Id, userId);
            return Results.NoContent();
        })
        .WithName("CancelMyBooking")
        .WithSummary("Cancel one of the authenticated user's future bookings")
        .Produces(StatusCodes.Status204NoContent)
        .ProducesProblem(StatusCodes.Status401Unauthorized)
        .ProducesProblem(StatusCodes.Status404NotFound);

        return group;
    }

    public static RouteGroupBuilder MapAdminApi(this RouteGroupBuilder group)
    {
        group.MapGet("/overview", async (AppDbContext db, IConfiguration configuration, CancellationToken ct) => Results.Ok(new
        {
            resources = await db.Resources.CountAsync(ct),
            timeSlots = await db.TimeSlots.CountAsync(ct),
            bookings = await db.Bookings.CountAsync(ct),
            users = await db.Users.CountAsync(ct),
            aiSkills = await db.AiSkills.CountAsync(ct),
            database = db.Database.ProviderName?.Contains("SqlServer", StringComparison.OrdinalIgnoreCase) == true ? "SQL Server" : "In-memory (development)",
            aiConfigured = !string.IsNullOrWhiteSpace(configuration["GROQ_API_KEY"] ?? configuration["Groq:ApiKey"]),
            signalR = string.IsNullOrWhiteSpace(configuration.GetConnectionString("AzureSignalR")) ? "Built-in SignalR" : "Azure SignalR"
        })).WithName("GetAdminOverview")
           .WithSummary("View system totals and service configuration (Admin)")
           .Produces(StatusCodes.Status200OK)
           .ProducesProblem(StatusCodes.Status401Unauthorized)
           .ProducesProblem(StatusCodes.Status403Forbidden);

        group.MapGet("/analytics", async (AppDbContext db, CancellationToken ct) =>
        {
            var now = DateTimeOffset.UtcNow;
            var todayUtc = DateOnly.FromDateTime(now.UtcDateTime);
            var fromUtc = new DateTimeOffset(todayUtc.AddDays(-13).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
            var untilUtc = now.AddDays(30);
            var recentBookings = db.Bookings.AsNoTracking()
                .Where(booking => booking.CreatedAtUtc >= fromUtc && booking.CreatedAtUtc <= now);

            var dailyCounts = await recentBookings
                .GroupBy(booking => booking.CreatedAtUtc.Date)
                .Select(day => new { Date = day.Key, Count = day.Count() })
                .ToListAsync(ct);
            var popularRooms = await recentBookings
                .GroupBy(booking => booking.TimeSlot.Resource.Name)
                .Select(room => new { Room = room.Key, Count = room.Count() })
                .OrderByDescending(room => room.Count)
                .ThenBy(room => room.Room)
                .Take(5)
                .ToListAsync(ct);
            var capacity = await db.TimeSlots.AsNoTracking()
                .Where(slot => slot.Resource.IsActive && slot.StartUtc >= now && slot.StartUtc < untilUtc)
                .GroupBy(_ => 1)
                .Select(slots => new { Total = slots.Count(), Booked = slots.Count(slot => slot.Booking != null) })
                .FirstOrDefaultAsync(ct);

            var days = Enumerable.Range(0, 14).Select(offset => todayUtc.AddDays(offset - 13)).Select(date => new
            {
                Date = date.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
                Count = dailyCounts.Where(day => DateOnly.FromDateTime(day.Date) == date).Select(day => day.Count).FirstOrDefault()
            });
            return Results.Ok(new
            {
                bookingsLast14Days = dailyCounts.Sum(day => day.Count),
                upcomingSlots = capacity?.Total ?? 0,
                bookedUpcomingSlots = capacity?.Booked ?? 0,
                upcomingUtilizationPercent = capacity is { Total: > 0 } ? Math.Round(capacity.Booked * 100d / capacity.Total, 1) : 0,
                dailyBookings = days,
                mostBookedRooms = popularRooms
            });
        })
        .WithName("GetAdminAnalytics")
        .WithSummary("View recent booking activity and upcoming room utilization (Admin)")
        .Produces(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status401Unauthorized)
        .ProducesProblem(StatusCodes.Status403Forbidden);

        group.MapGet("/bookings", async (AppDbContext db, CancellationToken ct) =>
        {
            var bookings = await db.Bookings
                .AsNoTracking()
                .Include(b => b.TimeSlot).ThenInclude(ts => ts.Resource)
                .Include(b => b.User)
                .OrderByDescending(b => b.CreatedAtUtc)
                .Select(b => new BookingDto(
                    b.Id,
                    b.TimeSlotId,
                    b.TimeSlot.StartUtc,
                    b.TimeSlot.EndUtc,
                    b.TimeSlot.ResourceId,
                    b.TimeSlot.Resource.Name,
                    b.UserId,
                    b.User.Email ?? string.Empty,
                    b.CreatedAtUtc,
                    b.IsAiGenerated))
                .ToListAsync(ct);

            return TypedResults.Ok(bookings);
        })
        .WithName("AdminGetAllBookings")
        .WithSummary("List all bookings (Admin)")
        .Produces<List<BookingDto>>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status401Unauthorized)
        .ProducesProblem(StatusCodes.Status403Forbidden);

        return group;
    }

    private static string GetResourceImageUrl(int id, bool hasImage) =>
        hasImage ? $"/api/resources/{id}/image" : "/images/rooms/no_image_rooms.png";

    private static UserDto ToUserDto(ApplicationUser user, IEnumerable<string> roles) => new(
        user.Id, user.Email ?? string.Empty, user.DisplayName, roles.ToList(), user.PhoneNumber,
        user.AvatarData is { Length: > 0 } ? "/api/auth/me/avatar" : null);

    private static string? GetUploadedImageContentType(byte[] data)
    {
        if (data.Length >= 3 && data[0] == 0xff && data[1] == 0xd8 && data[2] == 0xff) return "image/jpeg";
        if (data.Length >= 8 && data.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })) return "image/png";
        if (data.Length >= 12 && data.AsSpan(0, 4).SequenceEqual("RIFF"u8) && data.AsSpan(8, 4).SequenceEqual("WEBP"u8)) return "image/webp";
        return null;
    }

    private static bool IsSameOriginRequest(HttpContext http)
    {
        var source = http.Request.Headers.Origin.FirstOrDefault() ?? http.Request.Headers.Referer.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(source)) return true;
        return Uri.TryCreate(source, UriKind.Absolute, out var uri)
            && string.Equals(uri.Scheme, http.Request.Scheme, StringComparison.OrdinalIgnoreCase)
            && string.Equals(uri.Authority, http.Request.Host.Value, StringComparison.OrdinalIgnoreCase);
    }
}

public sealed record UpdateProfileRequest(string? DisplayName, string? PhoneNumber);
public sealed record ChangePasswordRequest(string? CurrentPassword, string? NewPassword);
public sealed record DirectorySearchRequest(string? Query);

public sealed record CreateTimeSlotRequest(DateTimeOffset StartUtc, DateTimeOffset EndUtc);

public static class ModelStateConversions
{
    public static Dictionary<string, string[]> ToModelStateDict(this IdentityResult result)
    {
        var dict = new Dictionary<string, string[]>(StringComparer.Ordinal);
        foreach (var e in result.Errors)
        {
            var key = string.IsNullOrWhiteSpace(e.Code) ? string.Empty : e.Code;
            if (!dict.ContainsKey(key)) dict[key] = [];
            dict[key] = [.. dict[key], e.Description];
        }
        return dict;
    }

    public static Dictionary<string, string[]> ToModelStateDict(this ValidationResult vr)
    {
        var dict = new Dictionary<string, string[]>(StringComparer.Ordinal);
        foreach (var f in vr.Errors)
        {
            var key = f.PropertyName ?? string.Empty;
            if (!dict.ContainsKey(key)) dict[key] = [];
            dict[key] = [.. dict[key], f.ErrorMessage];
        }
        return dict;
    }
}

public class RegisterRequestValidator : AbstractValidator<RegisterRequest>
{
    public RegisterRequestValidator()
    {
        RuleFor(x => x.Email).NotEmpty().EmailAddress();
        RuleFor(x => x.Password).NotEmpty().MinimumLength(6);
        RuleFor(x => x.DisplayName).NotEmpty().MaximumLength(200);
    }
}

public class LoginRequestValidator : AbstractValidator<LoginRequest>
{
    public LoginRequestValidator()
    {
        RuleFor(x => x.Email).NotEmpty().EmailAddress();
        RuleFor(x => x.Password).NotEmpty();
    }
}

public class CreateResourceRequestValidator : AbstractValidator<CreateResourceRequest>
{
    public CreateResourceRequestValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Description).MaximumLength(2000);
    }
}

public class UpdateResourceRequestValidator : AbstractValidator<UpdateResourceRequest>
{
    public UpdateResourceRequestValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Description).MaximumLength(2000);
    }
}

public class CreateBookingRequestValidator : AbstractValidator<CreateBookingRequest>
{
    public CreateBookingRequestValidator()
    {
        RuleFor(x => x.TimeSlotId).GreaterThan(0);
    }
}

public class CreateTimeSlotRequestValidator : AbstractValidator<CreateTimeSlotRequest>
{
    public CreateTimeSlotRequestValidator()
    {
        RuleFor(x => x.StartUtc).NotEmpty();
        RuleFor(x => x.EndUtc).NotEmpty();
        RuleFor(x => x.EndUtc).GreaterThan(x => x.StartUtc)
            .WithMessage("EndUtc must be after StartUtc");
    }
}

public partial class Program { }
