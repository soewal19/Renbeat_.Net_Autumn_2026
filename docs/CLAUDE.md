# CLAUDE.md — Reenbit Meeting Room Booking System

## 1. Purpose

This repository implements the Reenbit test task:

**Meeting Room Booking System with Concurrency Control**

The application manages a limited set of resources (meeting rooms) with fixed bookable time slots. Multiple users may attempt to book the same slot at approximately the same time.

The most important business invariant is:

> A time slot must never be double-booked.

The system must guarantee that concurrent booking requests for the same slot produce exactly one successful booking and clear conflict responses for competing requests. The solution must also propagate booking status changes to users currently viewing the affected resource schedule in real time.

This file is the primary development guidance for Claude Code. Follow these rules when creating, modifying, testing, reviewing, or refactoring the repository.

---

## 2. Task Requirements

The original task requires:

1. Azure environment setup.
2. Azure Web App for the application.
3. Azure SignalR Service.
4. Azure SQL Database.
5. Authentication and role-based authorization.
6. Two roles:
   - `User`
   - `Admin`
7. Regular users can:
   - view resources;
   - view resource schedules;
   - book available time slots.
8. Administrators can additionally:
   - create resources;
   - edit resources;
   - remove resources;
   - view all bookings across users.
9. Resources have a fixed set of bookable time slots.
10. Users must be able to distinguish free and booked slots.
11. Concurrent booking requests for the same slot must be handled explicitly and safely.
12. Exactly one competing request may succeed.
13. Other concurrent requests must receive a clear conflict response.
14. A naive unprotected `check -> insert` approach is forbidden.
15. The repository must contain an automated concurrency test that sends multiple simultaneous booking requests for the same slot and proves that exactly one booking is created.
16. Azure SignalR must be used for real-time schedule updates.
17. Users viewing a resource schedule must see booking status changes without refreshing the page.
18. The application must be deployed to Azure.
19. Git history must use atomic commits with meaningful descriptions.
20. The repository must contain `CLAUDE.md` reflecting Claude Code usage.
21. Source code must be documented and suitable for review.
22. Submission must provide the deployed application URL and GitHub repository URL.

The concurrency and real-time requirements are the primary technical focus of the task.

---

## 3. Technology Stack

Use the following stack unless a documented technical reason requires a change.

### Backend

- .NET 10
- ASP.NET Core
- Entity Framework Core
- ASP.NET Core Identity
- ASP.NET Core SignalR
- OpenAPI / Swagger
- ProblemDetails

### Frontend

- Blazor Web App with Interactive WebAssembly in `src/Client/RoomBooking.Client`
- Same-origin ASP.NET Core host in `src/Server/Components`
- Typed C# client services for API and SignalR; keep raw `HttpClient` calls out of Razor components
- Keep booking commands on HTTP; SignalR is for notifications and schedule groups
- Static assets under wwwroot are served by the primary Blazor application.

### Database

- Azure SQL Database
- SQL Server EF Core provider

### Real-time communication

- Azure SignalR Service

### Testing

- xUnit
- FluentAssertions
- ASP.NET Core `WebApplicationFactory`
- Testcontainers with SQL Server for database-backed integration/concurrency tests

### Azure / Infrastructure

- Azure App Service
- Azure SQL Database
- Azure SignalR Service
- Application Insights
- Managed Identity
- Azure Key Vault where appropriate
- Bicep Infrastructure as Code

### CI/CD

- GitHub Actions

### Development process

- Claude Code
- `CLAUDE.md`
- Atomic Git commits

---

## 4. Architectural Style

Use a **modular monolith** with clear feature boundaries and vertical-slice organization.

Do not introduce unnecessary microservices or distributed infrastructure.

Preferred high-level structure:

```text
src/
  Server/
    Features/
      Auth/
      Resources/
      Schedule/
      Bookings/
      Admin/
    Infrastructure/
      Persistence/
      Identity/
      SignalR/
    Hubs/
    Program.cs

  Client/
    Pages/
    Components/
    Layout/
    Services/
      Auth/
      Resources/
      Schedule/
      Bookings/
    Realtime/

tests/
  UnitTests/
  IntegrationTests/
  ConcurrencyTests/

infra/
  main.bicep
  modules/

docs/
  adr/
```

The exact folder names may be adjusted to fit the chosen .NET template, but the separation of responsibilities must remain clear.

### Architectural priorities

1. Correctness under concurrency.
2. Server-side security and authorization.
3. Testability.
4. Clear boundaries.
5. Simplicity and maintainability.
6. Reviewer-friendly code.

Do not add abstractions merely to make the project look larger.

---

## 5. Critical Concurrency Invariant

This is the most important rule in the repository.

### Invariant

```text
ONE TimeSlot = AT MOST ONE Booking
```

Enforce this invariant at the **database level**.

`Booking.TimeSlotId` must have a unique database index/constraint.

Example EF Core configuration:

```csharp
modelBuilder.Entity<Booking>()
    .HasIndex(x => x.TimeSlotId)
    .IsUnique();
```

The database is the final authority for booking uniqueness.

### Forbidden approach

Do NOT rely on:

```text
SELECT -> check free -> INSERT
```

as two separate unprotected operations.

This creates a race condition.

### Also forbidden as the correctness mechanism

- C# `lock`
- `Monitor`
- static locks
- in-memory mutexes
- frontend-only checks
- timing assumptions
- application instance state
- optimistic UI assumptions

The solution must remain correct if Azure App Service runs multiple instances.

### Preferred booking flow

```text
POST /api/bookings
        |
        v
   Validate request
        |
        v
   Validate slot
        |
        v
 Attempt atomic INSERT
        |
   +----+----------------+
   |                     |
Success              Unique conflict
   |                     |
   v                     v
Commit                 HTTP 409
   |
   v
Publish SignalR event
```

### Expected result for concurrent requests

For example, 20 concurrent requests to the same slot:

```text
1 x 201 Created
19 x 409 Conflict
1 database Booking row
```

The duplicate constraint violation must be translated into a controlled application-level conflict response.

Do not expose raw SQL exceptions.

---

## 6. Booking API Behavior

The booking endpoint should use a clear REST-style contract.

Suggested endpoint:

```http
POST /api/bookings
```

Success:

```http
201 Created
```

Concurrency conflict:

```http
409 Conflict
```

Use structured `ProblemDetails` for the conflict.

Example:

```json
{
  "type": "https://example.com/problems/slot-already-booked",
  "title": "Slot already booked",
  "status": 409,
  "detail": "The selected time slot was booked by another user.",
  "code": "SLOT_ALREADY_BOOKED"
}
```

Expected booking conflict is not a server failure and must not be returned as `500 Internal Server Error`.

---

## 7. Data Model

Minimum required entities:

### User

Managed by ASP.NET Core Identity.

### Resource

Suggested fields:

```text
Id
Name
Description
IsActive
CreatedAtUtc
UpdatedAtUtc
```

### TimeSlot

Suggested fields:

```text
Id
ResourceId
StartUtc
EndUtc
```

### Booking

Suggested fields:

```text
Id
TimeSlotId
UserId
CreatedAtUtc
```

Required database invariant:

```text
UNIQUE(Booking.TimeSlotId)
```

Prevent duplicate slots for the same resource/time interval where appropriate.

Persist timestamps in UTC. Prefer `DateTimeOffset` or an equivalent explicit UTC representation.

Do not use `DateTime.Now` for persisted business timestamps.

---

## 8. Authentication and Authorization

Use ASP.NET Core Identity.

Roles:

```text
User
Admin
```

Regular user permissions:

- view resources;
- view schedules;
- book available slots;
- view own bookings.

Admin permissions:

- all regular user capabilities;
- create resources;
- edit resources;
- remove resources;
- view all bookings.

Authorization must be enforced on the server.

The frontend must never be the only place where permissions are enforced.

Protect SignalR endpoints appropriately.

For multi-instance App Service deployments, persist the ASP.NET Core Data Protection key ring in storage shared by all instances. The application package directory is read-only when run from package.

Do not implement custom password hashing or custom cryptography.

Do not commit credentials, tokens, connection strings, or secrets.

---

## 9. Suggested API Surface

Authentication:

```http
POST /api/auth/register
POST /api/auth/login
POST /api/auth/logout
GET  /api/auth/me
```

Resources:

```http
GET    /api/resources
GET    /api/resources/{id}
POST   /api/resources
PUT    /api/resources/{id}
DELETE /api/resources/{id}
```

Schedule:

```http
GET /api/resources/{id}/schedule?date=YYYY-MM-DD
```

Bookings:

```http
POST /api/bookings
GET  /api/bookings/me
```

Admin:

```http
GET /api/admin/bookings
```

The exact endpoint implementation may differ, but the API must remain simple, predictable, and documented.

Use DTOs and validation. Use async APIs and cancellation tokens.

Keep controllers/endpoints thin.

---

## 10. SignalR and Real-Time Updates

Create a schedule hub, for example:

```text
/hubs/schedule
```

Use resource-specific groups:

```text
resource:{resourceId}
```

When a client opens a resource schedule:

```text
JoinResource(resourceId)
```

When leaving:

```text
LeaveResource(resourceId)
```

After a successful booking has been committed, publish an event such as:

```text
SlotBooked
```

Example payload:

```json
{
  "slotId": 42,
  "resourceId": 1,
  "bookingId": 91
}
```

### Critical ordering rule

Never publish a booking-success event before database persistence has succeeded.

Correct sequence:

```text
Validate
  -> Save booking
  -> Commit
  -> Notify SignalR clients
```

SignalR should primarily provide server-to-client realtime notifications. Booking commands should remain ordinary application/API commands unless there is a compelling architectural reason to do otherwise.

The UI must update without a browser refresh.

---

## 11. Frontend Rules

The primary client is the same-origin Blazor Web App in `src/Client/RoomBooking.Client`, hosted by ASP.NET Core. Keep authentication cookies and API calls on one origin without adding a separate deployment unit. The AI assistant is a Blazor workspace view.

Recommended pages/components:

- Login
- Register
- Resource list
- Resource schedule
- My bookings
- Admin resource management
- Admin booking overview

Keep API access and SignalR state in the frontend client module. Do not place server authorization rules in the UI; API endpoints remain authoritative.

The schedule UI should clearly distinguish:

```text
AVAILABLE
BOOKED
```

When a `409 Conflict` is received:

- show a user-friendly conflict message;
- update/refresh the affected slot state;
- never display raw SQL exceptions or stack traces.

Handle:

- loading states;
- empty states;
- API errors;
- authorization errors;
- concurrency conflicts;
- realtime disconnect/reconnect states.

Do not over-design the UI. Functional clarity is more important than visual complexity.

---

## 12. Testing Requirements

Testing is mandatory.

### Unit tests

Cover business rules and validation where appropriate.

### Integration tests

Cover at minimum:

- authentication;
- authorization;
- resources;
- schedules;
- bookings.

### Mandatory concurrency integration test

This test is critical and must use a real database behavior, not a mocked repository.

Recommended setup:

- xUnit;
- WebApplicationFactory;
- Testcontainers SQL Server;
- multiple authenticated HTTP clients/users.

Test scenario:

1. Create one resource.
2. Create one fixed time slot.
3. Create multiple users/clients.
4. Synchronize concurrent calls as much as reasonably possible.
5. Fire multiple `POST /api/bookings` requests at the same slot.
6. Assert exactly one `201 Created`.
7. Assert all competitors receive `409 Conflict`.
8. Query the actual database.
9. Assert exactly one booking exists for the slot.

Do not use arbitrary `Thread.Sleep` calls to manufacture a race.

Use task/barrier synchronization when useful so the test expresses concurrency intentionally.

The command to run all tests should be documented and simple, ideally:

```bash
dotnet test
```

---

## 13. Test Quality Rules

Tests must be:

- deterministic;
- isolated;
- readable;
- meaningful;
- representative of actual system behavior.

A test that only asserts "one request returned 201" is insufficient.

The concurrency test must verify the actual database invariant.

Preferred proof:

```text
Responses:
201 = 1
409 = N - 1

Database:
Booking rows for TimeSlotId = 1
count = 1
```

---

## 14. Azure Deployment

Deploy the application to Azure.

Preferred hosting model:

```text
One Azure App Service
    |
    +-- ASP.NET Core backend
    +-- Blazor Web App host and static assets
```

Additional services:

- Azure SQL Database
- Azure SignalR Service
- Application Insights
- Managed Identity
- Key Vault where appropriate

Prefer a single origin when practical to reduce unnecessary CORS and authentication complexity.

Use Bicep for infrastructure provisioning.

Suggested structure:

```text
infra/
  main.bicep
  modules/
    appservice.bicep
    sql.bicep
    signalr.bicep
    monitoring.bicep
    keyvault.bicep
```

Do not hard-code environment-specific secrets in Bicep or application settings committed to Git.

---

## 15. Infrastructure as Code

Infrastructure should be reproducible.

Bicep should define the required resources and configuration as far as practical.

Use parameters for:

- environment;
- Azure region;
- application name;
- resource naming.

Prefer Managed Identity for Azure-to-Azure authentication where supported.

Use Key Vault for secrets that genuinely require secret storage.

---

## 16. Observability

Use structured logging.

Useful booking diagnostics include:

- slot ID;
- resource ID;
- operation result;
- duration;
- conflict/success outcome.

Never log:

- passwords;
- access tokens;
- refresh tokens;
- connection strings;
- secrets.

Configure Application Insights for Azure deployment.

---

## 17. Error Handling

Use consistent HTTP semantics.

Typical responses:

```text
400 Bad Request
401 Unauthorized
403 Forbidden
404 Not Found
409 Conflict
422 Unprocessable Entity
500 Internal Server Error
```

Use `ProblemDetails` for API errors.

Expected concurrency conflicts must map to `409 Conflict`.

Use a global exception-handling strategy.

Do not expose stack traces or provider-specific database details in normal production responses.

---

## 18. Security Rules

Apply secure-by-default engineering practices:

- HTTPS only in deployed environments;
- server-side authorization;
- secure identity handling;
- input validation;
- EF Core parameterization;
- no secrets in source control;
- no custom password cryptography;
- least-privilege Azure access;
- safe logging;
- production configuration separated from source.

Do not implement security through UI visibility alone.

---

## 19. CI/CD

Create GitHub Actions workflows.

CI should run on pull requests and/or pushes to the main development branch.

Recommended stages:

```text
restore
build
test
frontend build
publish
```

The CI pipeline must include the concurrency integration test.

Deployment must not happen when mandatory tests fail.

Keep build and deployment workflows readable and maintainable.

---

## 20. Git Commit Strategy

Use atomic commits.

Good examples:

```text
chore: initialize solution
feat(auth): add identity and role-based authentication
feat(resources): add resources and schedules
feat(booking): enforce unique booking per slot
test(booking): add concurrent booking integration test
feat(realtime): add SignalR booking notifications
feat(client): add schedule UI
infra(azure): add Bicep deployment
ci: add GitHub Actions
docs: document concurrency architecture
```

Avoid a single giant commit for the whole project.

Each commit should represent one coherent change.

Explain why in the commit body when the architectural reasoning is important.

---

## 21. Architecture Decision Record

Create:

```text
docs/adr/001-concurrency-strategy.md
```

It should document:

- context;
- problem;
- decision;
- database unique constraint strategy;
- why naive check-then-insert is unsafe;
- why application-level locks are insufficient in a multi-instance Azure environment;
- why a distributed Redis lock is unnecessary for this invariant;
- consequences;
- testing strategy.

A second ADR may document the frontend/hosting choice if useful.

---

## 22. README Requirements

`README.md` must explain:

1. Project overview.
2. Functional requirements.
3. Architecture.
4. Technology stack.
5. Concurrency strategy.
6. Why double booking cannot happen.
7. Authentication and roles.
8. SignalR architecture.
9. Testing strategy.
10. Concurrency test.
11. Local setup.
12. Required environment variables/configuration.
13. Azure deployment.
14. Bicep infrastructure.
15. CI/CD.
16. Claude Code usage.
17. Trade-offs.
18. Known limitations.
19. Demo instructions.

The README should make it easy for a reviewer to understand and verify the critical requirements quickly.

---

## 23. Local Development

Document the minimal commands required to run locally.

Prefer a developer experience such as:

```bash
dotnet restore
dotnet build
dotnet test
dotnet run
```

If containers are required for integration tests, document the prerequisite and how tests start dependencies.

Do not require manual database preparation if it can reasonably be automated with migrations/Testcontainers.

---

## 24. Claude Code Working Rules

When modifying this repository:

1. Inspect existing code before changing it.
2. Preserve working behavior unless the change intentionally alters it.
3. Search for existing abstractions before creating new ones.
4. Prefer small, reversible changes.
5. Run relevant tests after changes.
6. Run the full test suite before declaring completion.
7. Never silently weaken the concurrency invariant.
8. Never replace a real integration test with a mock merely to make it pass.
9. Never commit secrets.
10. Update documentation when architecture changes.
11. Update ADRs when a significant architectural decision changes.
12. Keep code reviewability as a primary goal.

Before implementing a significant architectural change, explain the trade-off in the relevant code/documentation rather than introducing the change silently.

---

## 25. Things Not to Introduce Without Strong Reason

Do not add:

- microservices;
- Kubernetes;
- Kafka;
- Redis distributed locking;
- event sourcing;
- complicated CQRS infrastructure;
- large mediator abstractions;
- unnecessary generic repositories;
- excessive wrapper classes;
- complicated calendar engines;
- excessive frontend state management;
- heavy UI frameworks solely for visual polish.

The project is a test task. Prefer a small, coherent production-minded solution.

---

## 26. Production-Style Considerations

The application should be designed as a small production-quality system, but not over-engineered.

The database constraint protects the core invariant.

SignalR provides realtime propagation after persistence.

For a larger production system, a transactional outbox could be introduced to guarantee eventual publication of events after database commit. Do not implement an outbox automatically unless it is justified by the actual project scope.

Document meaningful limitations instead of hiding them.

---

## 27. Final Acceptance Checklist

Before declaring the project complete, verify all of the following:

- [ ] ASP.NET Core backend works.
- [x] Blazor frontend supports authentication, schedules, booking, admin resource management, and live updates.
- [ ] Authentication works.
- [ ] User role works.
- [ ] Admin role works.
- [ ] Resources can be created/edited/deleted by Admin.
- [ ] Regular users can view resources.
- [ ] Fixed time slots are available.
- [ ] Schedule clearly distinguishes free and booked slots.
- [ ] Booking works.
- [ ] `Booking.TimeSlotId` is unique in the database.
- [ ] Double booking is impossible under concurrent requests.
- [ ] Concurrent competitors receive HTTP 409.
- [ ] Expected booking races never become HTTP 500 responses.
- [ ] Automated concurrency integration test exists.
- [ ] Concurrency test proves exactly one booking in the real database.
- [ ] Azure SignalR is used.
- [ ] Active viewers receive booking status updates without page refresh.
- [ ] Azure SQL is configured.
- [ ] Azure App Service deployment is configured.
- [ ] Bicep infrastructure exists.
- [ ] Application Insights is configured where practical.
- [ ] Secrets are not committed.
- [ ] CI runs build and tests.
- [ ] CLAUDE.md exists and reflects the actual project.
- [ ] README is complete.
- [ ] ADR documents the concurrency strategy.
- [ ] Git history is atomic and understandable.

---

## 28. Final Engineering Principles

When choosing between two implementations:

1. Prefer correctness over convenience.
2. Prefer database-enforced invariants over timing assumptions.
3. Prefer server-side security over UI-only security.
4. Prefer deterministic tests over timing-based tests.
5. Prefer simple architecture over unnecessary technology.
6. Prefer explicit trade-offs over hidden behavior.
7. Prefer readable code over clever code.
8. Prefer one clear source of truth over multiple competing state stores.

The highest-priority rule is:

> The system must remain correct when multiple users attempt to book the same slot at the same time.

Any change that could weaken this guarantee requires explicit review and updated tests/documentation.

---

## 29. Optional AI, Skills, Help, C4 and OpenAPI

- Keep AI isolated from booking. `IAiAssistant` is provider-neutral at the endpoint boundary; Groq is optional and configured only on the server through `GROQ_API_KEY`, `GROQ_MODEL`, `GROQ_FALLBACK_MODEL`, `GROQ_ENDPOINT`, and `GROQ_TIMEOUT_SECONDS` (or `Groq:*` config). No startup dependency on credentials. Never commit provider keys.
- Groq model fallback may retry on 429, 5xx, transport failure, or timeout; do not retry invalid credentials or malformed requests. A second model under the same Groq organization does not evade organization-wide quotas.
- AI can use only explicit allowlisted tools. Read tools are scoped to the authenticated user. The `book_slot` write tool is enabled only for a clear, non-negated booking instruction; it must require exact room/local date/start time, revalidate an active future slot on the server, attribute the booking to the authenticated user, set `IsAiGenerated`, and rely on `UNIQUE(TimeSlotId)` as final authority. Never allow the model direct database, filesystem, secrets, arbitrary URL, or executable-code access. Publish SignalR only after persistence.
- Users can cancel only their own future bookings. The cancellation endpoint must return no booking details for a non-owner or past slot, and publish `SlotCancelled` only after the deletion commits. AI cannot cancel bookings.
- Skills are untrusted data/instructions. Admin-only mutation; generated/uploaded data is validated and shown as a draft, persisted inactive, then separately approved/activated. `.md`, `.txt`, and `.json` only; valid UTF-8; 64 KB max; never execute uploaded content.
- Core features must continue if Groq is missing, times out, fails or returns malformed output. Return safe 503 errors, keep sensitive prompts and provider responses out of logs, and never expose raw provider exceptions.
- Keep Mermaid C1–C4 files in `docs/architecture/`, README and this guide aligned with the shipped Blazor WebAssembly client and its typed API/SignalR services.
- Help and English documentation should explain direct AI booking requirements, confirmation-based availability suggestions, time-zone handling, conflict behavior, `Booked by AI` attribution, future-booking cancellation, realtime events, roles, Skills, OpenAPI and architecture. Swagger is available in Development only.
- Test provider failures, allowlist/user scoping, Skills lifecycle/authorization, malformed and unsupported uploads, and retain all original SQL concurrency tests unchanged.