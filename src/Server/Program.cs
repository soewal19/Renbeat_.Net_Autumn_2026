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
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.InMemory;
using RoomBooking.Server.Infrastructure.Identity;
using RoomBooking.Server.Infrastructure.Persistence;
using RoomBooking.Server.Infrastructure.Persistence.Entities;
using RoomBooking.Server.Infrastructure.SignalR.Hubs;
using RoomBooking.Server.Features.Ai;
using RoomBooking.Server.Features.Analytics;
using RoomBooking.Server.Features.Bookings;
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
        if (!builder.Environment.IsDevelopment())
            throw new InvalidOperationException("ConnectionStrings:DefaultConnection must be configured outside Development.");
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
builder.Services.AddScoped<BookingService>();
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

app.MapGroup("/api")
   .MapAnalyticsApi();

app.MapGet("/index.html", () => Results.Redirect("/", permanent: true)).AllowAnonymous();
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
