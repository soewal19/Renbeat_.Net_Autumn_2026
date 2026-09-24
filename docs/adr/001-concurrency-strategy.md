# ADR-001: Concurrency Strategy for Time Slot Booking

## Status
Accepted

## Context

The Reenbit Meeting Room Booking System allows multiple users to attempt booking the same fixed time slot simultaneously. The critical business invariant is:

> **A time slot must never be double-booked.**

A naive implementation pattern — `SELECT to check availability → INSERT if free` — contains an inherent TOCTOU (Time-Of-Check to Time-Of-Use) race condition. Two parallel requests can both read "free" before either writes, resulting in two bookings for the same slot.

The system is hosted on Azure App Service, which may run multiple application instances behind a load balancer. Any solution that relies solely on in-process state (C# `lock`, `Monitor`, static sync objects, in-memory dictionaries) will fail when more than one instance is running.

## Problem

Guarantee exactly-zero double bookings under concurrent load, regardless of the number of application instances, without introducing unnecessary distributed infrastructure.

## Decision

### Primary Mechanism: Database UNIQUE Constraint on `Booking.TimeSlotId`

The **database is the single source of truth** for booking uniqueness.

```sql
CREATE UNIQUE INDEX IX_Bookings_TimeSlotId_Unique ON Bookings (TimeSlotId);
```

EF Core configuration in [`BookingConfiguration.cs`](file:///e:/TestTask/ReenBeat2026Autumn2026/src/Server/Infrastructure/Persistence/Configurations/BookingConfiguration.cs):

```csharp
builder.HasIndex(b => b.TimeSlotId)
       .IsUnique()
       .HasDatabaseName("IX_Bookings_TimeSlotId_Unique");
```

### Booking Flow

1. The application performs a single atomic `INSERT` of the `Booking` row.
2. If the slot is free → insert succeeds → HTTP **201 Created** → SignalR notification published **after** commit.
3. If the slot is already booked → the database rejects the insert with error 2601/2627 → the application catches `DbUpdateException` → returns HTTP **409 Conflict** with a structured `ProblemDetails` body containing `code: SLOT_ALREADY_BOOKED`.

The application never exposes raw SQL exceptions to clients.

### Why Naive `check → then insert` is unsafe

Two requests executing in parallel on different connection pools (or different app instances) both observe `slot.Booking == null` and both proceed to `INSERT`. Without the database-enforced unique index, both inserts succeed. This is the classic race condition that the task requirements explicitly forbid.

### Why application-level locks are insufficient

- In-process locks (`lock ()`, `SemaphoreSlim`) only serialize requests within one CLR process. With N application instances, N requests still race at the database.
- Static state does not survive process recycling or cross-VM scale-out.
- Relying on sticky sessions / single-instance hosting is operationally fragile and would silently break if the App Service Plan scales out.

### Why Redis / distributed locks are not used here

For this specific invariant (at-most-one per slot), a relational UNIQUE constraint is:

- Simpler: zero infrastructure dependencies beyond SQL Server (already required).
- Correct by construction: the engine serializes index key inserts regardless of caller.
- Cheaper: no extra network round-trip, no lock TTL tuning, no orphaned lock recovery.
- Auditable: any violation attempt is visible in the database log.

A distributed lock (e.g., Redlock) would add complexity without improving correctness for a single-row uniqueness guarantee.

### Why optimistic concurrency (row-version) alone is insufficient

Optimistic concurrency on `TimeSlot` would still require both writing a booking AND flipping a flag on `TimeSlot` inside a transaction, plus extra retry logic. The unique index is simpler, covers the same invariant, and catches misuse paths. We use the unique index as the primary guard.

## Consequences

**Positive:**
- Invariant holds under any degree of concurrency and any number of App Service instances.
- Zero race windows at the application layer.
- Conflict responses are deterministic and well-defined (HTTP 409).
- The solution is reviewable in a single place (EF configuration + one `when` clause in booking endpoint).

**Negative / trade-offs:**
- Unique constraint violation does cause a first-chance `DbUpdateException` caught by a filter; this is the standard EF Core pattern for this scenario and is explicitly logged at `Information` level.
- Booking insert is serialized at the database index leaf for the same slot, reducing theoretical peak throughput vs. a check-then-insert pattern — entirely acceptable for the expected booking volume of meeting rooms.
- In-memory provider does not emulate unique constraints perfectly. Integration/concurrency tests **must** use a real SQL Server database (Testcontainers).

## Testing Strategy

### Mandatory concurrency integration test

Implemented in [`ConcurrencyBookingTests.cs`](file:///e:/TestTask/ReenBeat2026Autumn2026/tests/ConcurrencyTests/).

Scenario:
1. Start a Testcontainers MS SQL container.
2. Seed one resource and one fixed time slot.
3. Register N distinct users, each with their own authenticated `HttpClient` (cookie container per client).
4. Use a `TaskCompletionSource` (or `Barrier`) to synchronize all clients so each `POST /api/bookings` request fires at the same moment.
5. Collect all HTTP response status codes.
6. **Assert:** exactly 1 response == `201 Created`; exactly N-1 responses == `409 Conflict`.
7. Query the actual database.
8. **Assert:** `COUNT(*) FROM Bookings WHERE TimeSlotId = @id == 1`.

The test intentionally uses a real SQL Server, not mocked repositories, because the invariant is enforced by the database.

### Other tests
- Unit tests cover validators and the helper that detects unique-constraint exceptions.
- Integration tests cover auth, resource CRUD authorization, schedule retrieval, single-user happy-path booking.
- All tests are runnable via `dotnet test`.

## Related Code

- Confliguration: [BookingConfiguration.cs](file:///e:/TestTask/ReenBeat2026Autumn2026/src/Server/Infrastructure/Persistence/Configurations/BookingConfiguration.cs)
- Detection helper: [DbConcurrencyHelper](file:///e:/TestTask/ReenBeat2026Autumn2026/src/Server/Program.cs#L155-L178)
- Booking endpoint with conflict handling: [MapBookingsApi](file:///e:/TestTask/ReenBeat2026Autumn2026/src/Server/Program.cs#L490-L607)
- Global ProblemDetails mapping: [Program.cs ProblemDetails](file:///e:/TestTask/ReenBeat2026Autumn2026/src/Server/Program.cs#L38-L53)
