using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RoomBooking.Server.Infrastructure.Persistence;
using RoomBooking.Server.Infrastructure.Persistence.Entities;
using RoomBooking.Shared.Dtos.Auth;
using RoomBooking.Shared.Dtos.Bookings;
using RoomBooking.Shared.Dtos.Resources;
using RoomBooking.Shared.Dtos.Schedule;

namespace RoomBooking.ConcurrencyTests;

[Collection("ConcurrencyTests")]
public class ConcurrencyBookingTests
{
    private readonly ConcurrencyTestWebFactory _factory;
    private const int ConcurrentRequestCount = 20;

    public ConcurrencyBookingTests(ConcurrencyTestWebFactory factory)
    {
        _factory = factory;
    }

    /// <summary>
    /// MANDATORY CONCURRENCY TEST.
    ///
    /// Scenario:
    /// 1. Create one resource.
    /// 2. Create one fixed time slot.
    /// 3. Create N distinct authenticated HTTP client users.
    /// 4. Synchronize all clients to fire POST /api/bookings at the same slot simultaneously.
    /// 5. Assert exactly 1 x 201 Created.
    /// 6. Assert all remaining N-1 x 409 Conflict.
    /// 7. Query the actual SQL Server database.
    /// 8. Assert exactly 1 Booking row exists for the slot.
    ///
    /// This uses a real SQL Server (Testcontainers) because the uniqueness invariant
    /// is enforced by the database UNIQUE index, not by application code.
    /// </summary>
    [Fact]
    public async Task MultipleConcurrentBookings_SameSlot_ExactlyOneSucceeds_RestConflict()
    {
        // === Step 0: ensure DB is created via warm-up call ===
        var warmUp = _factory.CreateClient();
        await warmUp.GetAsync("/api/resources");

        int resourceId;
        int slotId;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var resource = new Resource
            {
                Name = "Concurrency Room",
                Description = "Room used by concurrency tests",
                IsActive = true,
                CreatedAtUtc = DateTimeOffset.UtcNow,
                UpdatedAtUtc = DateTimeOffset.UtcNow
            };
            db.Resources.Add(resource);
            await db.SaveChangesAsync();
            resourceId = resource.Id;

            var slotStart = new DateTimeOffset(2030, 1, 15, 10, 0, 0, TimeSpan.Zero);
            var slot = new TimeSlot
            {
                ResourceId = resourceId,
                StartUtc = slotStart,
                EndUtc = slotStart.AddHours(1)
            };
            db.TimeSlots.Add(slot);
            await db.SaveChangesAsync();
            slotId = slot.Id;
        }

        // === Step 1: create N users, each with their own HttpClient (separate cookies) ===
        var clients = new List<HttpClient>(ConcurrentRequestCount);
        for (int i = 0; i < ConcurrentRequestCount; i++)
        {
            var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
            {
                AllowAutoRedirect = false,
                HandleCookies = true
            });

            var email = $"concurrent-user-{i:D3}-{Guid.NewGuid():N}@test.com";
            var register = new RegisterRequest(email, "Password123!", $"User {i}");
            var regResp = await client.PostAsJsonAsync("/api/auth/register", register);
            regResp.IsSuccessStatusCode.Should()
                .BeTrue($"registration for user {i} should succeed: {await regResp.Content.ReadAsStringAsync()}");

            var login = new LoginRequest(email, "Password123!");
            var loginResp = await client.PostAsJsonAsync("/api/auth/login", login);
            loginResp.IsSuccessStatusCode.Should()
                .BeTrue($"login for user {i} should succeed: {await loginResp.Content.ReadAsStringAsync()}");

            clients.Add(client);
        }

        // === Step 2: synchronize all clients to fire at the same moment ===
        var syncTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var readyTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var readyCount = 0;
        var slotIdCopy = slotId;

        var bookingTasks = clients.Select(async (http, i) =>
        {
            // Wait until every client is ready before releasing the requests together.
            if (Interlocked.Increment(ref readyCount) == clients.Count)
                readyTcs.TrySetResult();

            await syncTcs.Task;

            try
            {
                var req = new CreateBookingRequest(slotIdCopy);
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                var response = await http.PostAsJsonAsync("/api/bookings", req, cts.Token);
                var body = await response.Content.ReadAsStringAsync(cts.Token);
                return (Index: i, Status: response.StatusCode, Body: body);
            }
            catch (Exception ex)
            {
                return (Index: i, Status: (HttpStatusCode)0, Body: ex.ToString());
            }
        }).ToArray();

        await readyTcs.Task.WaitAsync(TimeSpan.FromSeconds(10));
        syncTcs.TrySetResult();

        var results = await Task.WhenAll(bookingTasks);

        // === Step 3: verify HTTP responses ===
        var createdCount = results.Count(r => r.Status == HttpStatusCode.Created);
        var conflictCount = results.Count(r => r.Status == HttpStatusCode.Conflict);
        var otherCount = results.Length - createdCount - conflictCount;

        createdCount.Should().Be(1,
            $"exactly one concurrent booking should succeed. " +
            $"Got: Created={createdCount}, Conflict={conflictCount}, Other={otherCount}. " +
            $"Details: {string.Join(" | ", results.Select(r => $"[{r.Index}:{r.Status}] {(r.Body.Length > 200 ? r.Body[..200] : r.Body)}"))}");

        conflictCount.Should().Be(ConcurrentRequestCount - 1,
            $"the remaining {ConcurrentRequestCount - 1} requests should all receive HTTP 409 Conflict. " +
            $"Non-conflict non-success responses: {otherCount}");

        // === Step 4: verify the actual database invariant ===
        int dbBookingCount;
        Booking? singleBooking = null;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            dbBookingCount = await db.Bookings.CountAsync(b => b.TimeSlotId == slotId);
            singleBooking = await db.Bookings
                .AsNoTracking()
                .Include(b => b.User)
                .Include(b => b.TimeSlot)
                .FirstOrDefaultAsync(b => b.TimeSlotId == slotId);
        }

        dbBookingCount.Should().Be(1,
            "the real SQL Server database must contain exactly 1 Booking row for the slot. " +
            $"Found {dbBookingCount} — the UNIQUE index on Booking.TimeSlotId must have saved us.");

        singleBooking.Should().NotBeNull();
        singleBooking!.TimeSlotId.Should().Be(slotId);
        singleBooking.UserId.Should().NotBeNullOrWhiteSpace();
        singleBooking.CreatedAtUtc.Should().BeAfter(DateTimeOffset.UtcNow.AddMinutes(-5));
    }

    [Fact]
    public async Task TwoSequentialBookings_SameSlot_SecondIsConflict()
    {
        var warmUp = _factory.CreateClient();
        await warmUp.GetAsync("/api/resources");

        int slotId;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var resource = new Resource
            {
                Name = "Sequential Room",
                Description = "Sequential test",
                IsActive = true,
                CreatedAtUtc = DateTimeOffset.UtcNow,
                UpdatedAtUtc = DateTimeOffset.UtcNow
            };
            db.Resources.Add(resource);
            await db.SaveChangesAsync();

            var slotStart = new DateTimeOffset(2030, 2, 1, 9, 0, 0, TimeSpan.Zero);
            var slot = new TimeSlot
            {
                ResourceId = resource.Id,
                StartUtc = slotStart,
                EndUtc = slotStart.AddHours(1)
            };
            db.TimeSlots.Add(slot);
            await db.SaveChangesAsync();
            slotId = slot.Id;
        }

        async Task<HttpClient> NewClientAsync(string email)
        {
            var c = _factory.CreateClient();
            var registration = await c.PostAsJsonAsync("/api/auth/register",
                new RegisterRequest(email, "Password123!", "Seq User"));
            registration.EnsureSuccessStatusCode();
            var login = await c.PostAsJsonAsync("/api/auth/login",
                new LoginRequest(email, "Password123!"));
            login.EnsureSuccessStatusCode();
            return c;
        }

        var clientA = await NewClientAsync($"seq-a-{Guid.NewGuid():N}@test.com");
        var clientB = await NewClientAsync($"seq-b-{Guid.NewGuid():N}@test.com");

        var first = await clientA.PostAsJsonAsync("/api/bookings", new CreateBookingRequest(slotId));
        first.StatusCode.Should().Be(HttpStatusCode.Created);

        var second = await clientB.PostAsJsonAsync("/api/bookings", new CreateBookingRequest(slotId));
        second.StatusCode.Should().Be(HttpStatusCode.Conflict,
            $"second sequential booking must be 409, got {second.StatusCode}: {await second.Content.ReadAsStringAsync()}");

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            (await db.Bookings.CountAsync(b => b.TimeSlotId == slotId)).Should().Be(1);
        }
    }
}
