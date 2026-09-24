using System.Security.Claims;
using FluentValidation;
using FluentValidation.Results;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.InMemory;
using Microsoft.OpenApi;
using RoomBooking.Server.Infrastructure.Identity;
using RoomBooking.Server.Infrastructure.Persistence;
using RoomBooking.Server.Infrastructure.Persistence.Entities;
using RoomBooking.Server.Infrastructure.SignalR.Hubs;
using RoomBooking.Shared.Dtos.Auth;
using RoomBooking.Shared.Dtos.Bookings;
using RoomBooking.Shared.Dtos.Resources;
using RoomBooking.Shared.Dtos.Schedule;
using RoomBooking.Shared.SignalR;
using LoginRequest = RoomBooking.Shared.Dtos.Auth.LoginRequest;
using RegisterRequest = RoomBooking.Shared.Dtos.Auth.RegisterRequest;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "RoomBooking API",
        Version = "v1",
        Description = "Meeting Room Booking System with concurrency control"
    });
});

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

builder.Services.AddAuthorizationBuilder()
    .AddPolicy(AppRoles.Admin, policy => policy.RequireRole(AppRoles.Admin));

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
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowAll", policy =>
        policy.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader().WithExposedHeaders("*"));
});

builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ICurrentUserService, CurrentUserService>();

var app = builder.Build();

app.UseExceptionHandler();
app.UseStatusCodePages();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI(c => c.DisplayRequestDuration());
}

if (!app.Environment.IsDevelopment())
{
    app.UseHttpsRedirection();
}
app.UseCors("AllowAll");
app.UseDefaultFiles();
app.UseStaticFiles();
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();

app.MapHub<ScheduleHub>("/hubs/schedule")
   .RequireAuthorization();

app.MapGroup("/api/auth")
   .WithTags("Auth")
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
   .RequireAuthorization(AppRoles.Admin)
   .MapAdminApi();

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
            return TypedResults.Ok(new UserDto(user.Id, user.Email!, user.DisplayName, roles.ToList()));
        })
        .WithName("Register")
        .WithOpenApi();

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
            return TypedResults.Ok(new UserDto(user.Id, user.Email!, user.DisplayName, roles.ToList()));
        })
        .WithName("Login")
        .WithOpenApi();

        group.MapPost("/logout", async (SignInManager<ApplicationUser> signIn) =>
        {
            await signIn.SignOutAsync();
            return TypedResults.Ok();
        })
        .WithName("Logout")
        .WithOpenApi();

        group.MapGet("/me", [Authorize] async Task<Results<Ok<UserDto>, UnauthorizedHttpResult>> (
            UserManager<ApplicationUser> userManager,
            ICurrentUserService currentUser,
            CancellationToken ct) =>
        {
            var user = await currentUser.GetUserAsync(ct);
            if (user is null) return TypedResults.Unauthorized();

            var roles = await userManager.GetRolesAsync(user);
            return TypedResults.Ok(new UserDto(user.Id, user.Email!, user.DisplayName, roles.ToList()));
        })
        .WithName("Me")
        .WithOpenApi();

        return group;
    }

    public static RouteGroupBuilder MapResourcesApi(this RouteGroupBuilder group)
    {
        group.MapGet("/", async (AppDbContext db, CancellationToken ct) =>
        {
            var resources = await db.Resources
                .OrderBy(r => r.Name)
                .Select(r => new ResourceDto(r.Id, r.Name, r.Description, r.IsActive, r.CreatedAtUtc, r.UpdatedAtUtc))
                .ToListAsync(ct);
            return TypedResults.Ok(resources);
        })
        .WithName("GetResources")
        .WithOpenApi();

        group.MapGet("/{id:int}", async Task<Results<Ok<ResourceDto>, NotFound>> (
            int id, AppDbContext db, CancellationToken ct) =>
        {
            var r = await db.Resources.FindAsync([id], ct);
            if (r is null) return TypedResults.NotFound();
            return TypedResults.Ok(new ResourceDto(r.Id, r.Name, r.Description, r.IsActive, r.CreatedAtUtc, r.UpdatedAtUtc));
        })
        .WithName("GetResourceById")
        .WithOpenApi();

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

            var dto = new ResourceDto(resource.Id, resource.Name, resource.Description, resource.IsActive, resource.CreatedAtUtc, resource.UpdatedAtUtc);
            return TypedResults.Created($"/api/resources/{resource.Id}", dto);
        })
        .WithName("CreateResource")
        .WithOpenApi();

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
        .WithOpenApi();

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
        .WithOpenApi();

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
        .WithOpenApi();

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
        .WithOpenApi();

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
                CreatedAtUtc = DateTimeOffset.UtcNow
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
                created.CreatedAtUtc);

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
        .WithOpenApi();

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
                    b.CreatedAtUtc))
                .ToListAsync(ct);

            return TypedResults.Ok(bookings);
        })
        .WithName("GetMyBookings")
        .WithOpenApi();

        return group;
    }

    public static RouteGroupBuilder MapAdminApi(this RouteGroupBuilder group)
    {
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
                    b.CreatedAtUtc))
                .ToListAsync(ct);

            return TypedResults.Ok(bookings);
        })
        .WithName("AdminGetAllBookings")
        .WithOpenApi();

        return group;
    }
}

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
