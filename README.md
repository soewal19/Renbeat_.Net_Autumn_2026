# Reenbit Meeting Room Booking System

A modular monolith for booking meeting rooms with database-enforced concurrency and real-time schedule updates. The application uses ASP.NET Core, Blazor WebAssembly, Azure SQL Database, and Azure SignalR Service.

## Documentation

- [User and administrator guide](docs/user-guide.md)
- [AI-assisted booking guide](docs/ai-assisted-booking.md)
- [C4 architecture documentation](docs/architecture/README.md): context, containers, components, and deployment
- [Concurrency strategy ADR](docs/adr/001-concurrency-strategy.md)
- [Requirements specification and acceptance map](docs/specs/meeting-room-system.md)

Keep local credentials in .NET User Secrets or environment variables. Never commit secrets.

## 1. Project overview

Roomly manages meeting rooms and their explicitly defined bookable time slots. Users browse rooms and schedules, book free slots, see their bookings, and receive live schedule updates. Administrators manage rooms and availability, review all bookings, and access analytics.

The application uses ASP.NET Core Identity with `User` and `Admin` roles. Authorization is enforced by the server.

## 2. Features and permissions

| Feature | User | Admin |
|---|:---:|:---:|
| Browse rooms and schedules | Yes | Yes |
| Book available slots and view personal bookings | Yes | Yes |
| Manage rooms, images, and availability | No | Yes |
| View all bookings and analytics | No | Yes |
| Use optional AI room and booking assistance | Yes | Yes |
| Manage AI skills | No | Yes |

Room images accept JPEG, PNG, or WebP up to 10 MB. The UI uses `wwwroot/images/rooms/no_image_rooms.png` when a room has no uploaded image.

## 3. Architecture

```text
Browser
  └─ Blazor WebAssembly UI (typed C# clients)
      ├─ HTTP ──> ASP.NET Core API and Identity
      └─ SignalR ──> Schedule hub
                       │
ASP.NET Core modular monolith
  └─ Entity Framework Core ──> Azure SQL Database

Azure App Service hosts the API, UI, and static assets on one origin.
Azure SignalR Service provides the production real-time transport.
```

Key project areas:

- `src/Server/`: ASP.NET Core application, API features, persistence, Identity, and SignalR.
- `src/Client/RoomBooking.Client/`: Blazor WebAssembly UI, typed API clients, and real-time client.
- `src/Shared/`: API DTOs and shared SignalR contracts.
- `tests/UnitTests/`, `tests/IntegrationTests/`, and `tests/ConcurrencyTests/`: unit, API, and SQL-backed concurrency coverage.
- `infra/`: Azure Bicep templates.
- `.github/workflows/`: CI and deployment workflows.
- `docs/`: user guidance, architecture, ADRs, and SDD specification.

## 4. Technology stack

| Area | Technology |
|---|---|
| Backend | .NET 10, ASP.NET Core Minimal APIs, ProblemDetails, OpenAPI |
| Frontend | Blazor Web App, Interactive WebAssembly, Razor components, typed C# clients |
| Persistence | Entity Framework Core, SQL Server provider, migrations |
| Authentication | ASP.NET Core Identity, secure cookies, role-based authorization |
| Real-time | ASP.NET Core SignalR and Azure SignalR Service |
| Validation | FluentValidation |
| Testing | xUnit, FluentAssertions, WebApplicationFactory, Testcontainers for SQL Server |
| Infrastructure | Azure App Service, Azure SQL, Azure SignalR, Key Vault, Application Insights, Bicep |
| CI/CD | GitHub Actions |

## 5. Booking concurrency guarantee

The invariant is **one time slot can have at most one booking**. SQL Server is the final authority: `Booking.TimeSlotId` has a unique database index.

```csharp
builder.HasIndex(booking => booking.TimeSlotId)
    .IsUnique()
    .HasDatabaseName("IX_Bookings_TimeSlotId_Unique");
```

The booking flow validates the slot, attempts the insert, and lets the unique constraint resolve races. SQL duplicate-key errors (2601/2627) are mapped to HTTP 409 Conflict with a safe ProblemDetails response. A competing request is never silently overwritten and does not become an expected HTTP 500.

The application does not use a check-then-insert correctness path, in-memory locks, or instance-local state. This remains correct with multiple App Service instances. SignalR notifications are published only after the database confirms the booking.

See [ADR 001](docs/adr/001-concurrency-strategy.md) for the decision and trade-offs.

## 6. Authentication and authorization

ASP.NET Core Identity manages passwords and account credentials. New registrations receive the `User` role. An optional seed administrator is created from server-side configuration (`Seed:AdminEmail` and `Seed:AdminPassword`). Admin endpoints require the `Admin` role on the server; hiding UI controls is not used as a security boundary.

The app uses same-origin secure authentication cookies. It does not put authentication secrets in browser local storage.

## 7. Real-time updates

Authorized clients connect to `/hubs/schedule` and join a resource group with `JoinResource(resourceId)`. They leave with `LeaveResource(resourceId)`. After a successful booking or cancellation commit, viewers of the affected resource receive a schedule event and update without a page refresh.

Public schedule notifications do not disclose the booker's identity. Admin booking reports can include user details. If the process stops after a database commit but before publishing a notification, a production system with stronger delivery guarantees may need a transactional outbox.

## 8. API overview

The REST API includes:

- Authentication: `/api/auth/register`, `/api/auth/login`, `/api/auth/logout`, `/api/auth/me`
- Rooms: `/api/resources` and `/api/resources/{id}`
- Schedules: `/api/resources/{id}/schedule?date=YYYY-MM-DD`
- Bookings: `/api/bookings`, `/api/bookings/me`, and `/api/bookings/{id}`
- Admin: `/api/admin/bookings` and analytics endpoints
- Optional AI: `/api/ai/chat` and `/api/ai/status`

In Development, Swagger UI is available at `/swagger`, and the OpenAPI document is at `/openapi/v1.json`. Authenticate in Swagger with `POST /api/auth/login` first so the same-origin Identity cookie is sent with later requests.

## 9. Testing

Run the full suite with:

```bash
dotnet test
```

The concurrency test uses SQL Server through Testcontainers, separate authenticated HTTP clients, and synchronized parallel requests. It asserts that 20 simultaneous attempts for one slot produce exactly one `201 Created`, nineteen `409 Conflict` responses, and exactly one corresponding database row. Docker must be available.

Additional coverage includes API authentication and authorization, resource and schedule behavior, booking conflicts, AI booking attribution and tool restrictions, and AI skill management.

## 10. Local development

Requirements: .NET 10 SDK. Docker is required for SQL Server integration and concurrency tests. LocalDB or another SQL Server can also be configured for local development.

```bash
dotnet restore
dotnet build
dotnet run --project src/Server/RoomBooking.Server.csproj
```

The development launch profile uses `https://localhost:7274/`. Swagger is at `https://localhost:7274/swagger`.

### Persistent Docker database and demo data

From the repository root, run:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/seed-demo.ps1
```

The script starts SQL Server in the `roombooking-sql` container, persists database files in the `roombooking-sql-data` Docker volume, and stores the connection string in .NET User Secrets. It applies migrations, imports the versioned directory fixture, and starts the application at `http://127.0.0.1:5155`. The fixture contains 20 directory users, 20 rooms, and three future slots per room. Directory-only demo users have no passwords and cannot sign in. Re-running the seed is idempotent.

### Reviewer demo

1. Configure a unique administrator email and strong password with .NET User Secrets. Do not use credentials from examples.
2. Start the app and sign in as the configured Admin.
3. Create a room and future slots.
4. Sign in as a regular user in a second browser profile and attempt to book the same slot at nearly the same time.
5. Confirm one request succeeds, the competing request receives 409, and both open schedules update through SignalR.
6. For a repeatable SQL-backed demonstration, run:
   ```bash
   dotnet test tests/ConcurrencyTests/RoomBooking.ConcurrencyTests.csproj --configuration Release
   ```

## 11. Configuration

| Key | Purpose |
|---|---|
| `ConnectionStrings:DefaultConnection` | SQL Server connection. Required outside Development. |
| `ConnectionStrings:AzureSignalR` | Azure SignalR connection. Leave empty for local SignalR during development. |
| `Seed:AdminEmail`, `Seed:AdminPassword` | Optional seed administrator credentials. |
| `Seed:DemoData` | Opt in to importing sample rooms, users, and slots from `wwwroot/data/directory.json`. Disabled by default. |
| `ASPNETCORE_ENVIRONMENT` | `Development`, `Staging`, or `Production`. |
| `APPLICATIONINSIGHTS_CONNECTION_STRING` | Enables Azure Monitor OpenTelemetry export to Application Insights. |
| `GROQ_API_KEY` | Optional server-side credential for AI features. |

Room image data and its validated MIME type are stored in SQL Server and served through `GET /api/resources/{id}/image`. Admins upload images through `POST /api/resources/{id}/image`. Use Managed Identity and Key Vault for production secrets where available.

## 12. Azure deployment and infrastructure

**Deployment URL:** No Azure deployment has been verified in this workspace. Deployment requires an authenticated Azure subscription and configured GitHub Actions secrets.

The Bicep template at [`infra/main.bicep`](infra/main.bicep) and its modules provision the App Service, Azure SQL, Azure SignalR, Application Insights, and Key Vault. App Service hosts the API, Blazor UI, and static content on one origin. Managed Identity and Key Vault references are used for runtime secrets.

Example subscription deployment:

```bash
az login
az account set --subscription <subscription-id>
az deployment sub create \
  --location westeurope \
  --template-file infra/main.bicep \
  --parameters environmentName=dev appName=roombooking \
    sqlAdminPassword='<unique-sql-password>' adminPassword='<unique-admin-password>'
```

Set `seedDemoData=true` only for a demo environment. The import is idempotent and creates directory-only accounts without passwords; leave it disabled for production.

To deploy through GitHub Actions, configure the repository secrets `AZURE_CREDENTIALS`, `AZURE_SUBSCRIPTION_ID`, `SQL_ADMIN_PASSWORD`, and `APP_ADMIN_PASSWORD`. `GROQ_API_KEY` is optional. The Azure identity needs subscription deployment permissions and permission to create role assignments for the App Service managed identity. The deploy workflow runs only after required build and test jobs pass.

The App Service stores its Data Protection key ring in persistent shared storage so authentication cookies work across instances and deployments. Forwarded headers are configured for HTTPS redirection behind the App Service proxy.

## 13. CI/CD

The GitHub Actions workflow runs restore, Release build, unit tests, integration tests, SQL-backed concurrency tests, and publish on pushes and pull requests. Azure deployment is a manual workflow dispatch and is gated on successful required checks. Integration and concurrency jobs require Docker.

## 14. Claude Code and specification-driven development

The repository includes [CLAUDE.md](CLAUDE.md) and [docs/CLAUDE.md](docs/CLAUDE.md) with architecture guidance, commands, coding conventions, security rules, and the booking concurrency invariant. The database unique constraint is explicitly documented as the final authority for booking uniqueness. SignalR success events are emitted only after persistence succeeds.

The project uses a lightweight SDD approach. Requirements, design decisions, implementation notes, and acceptance evidence are tracked in [the meeting-room specification](docs/specs/meeting-room-system.md). Changes to the concurrency strategy must update the ADR and engineering guidance and pass the SQL-backed concurrency test.

## 15. Optional AI assistant

The optional server-side Groq integration can answer room and availability questions and book a slot when a user explicitly requests a booking with an exact room, local date, and start time. Availability suggestions are read-only until the user confirms. AI and manual booking share `BookingService` and the same unique database index. AI-created bookings are marked and labeled **Booked by AI**.

AI tools are allowlisted and limited to room listing, schedule lookup, personal booking lookup, policy explanation, proposal, and guarded booking. The AI cannot execute arbitrary code or SQL, access the filesystem, or manage another user's bookings. Skills are plain text, admin-managed guidance; they cannot authorize tools or override server-side rules.

Configure `Groq:ApiKey` using User Secrets locally or `GROQ_API_KEY` in Azure. The key remains server-side. The configured primary and fallback models use the same provider credentials; model fallback cannot bypass an account-wide quota or invalid credentials. AI is optional: room browsing, manual booking, concurrency control, cancellation, and SignalR remain available without it. See [the AI guide](docs/ai-assisted-booking.md) for more detail.

## 16. Architecture trade-offs and limitations

- A SQL Server unique index provides a simple, multi-instance-safe booking invariant. A duplicate-key exception is an expected conflict path and maps to 409.
- A modular monolith fits the assignment and keeps deployment and review straightforward.
- SignalR groups are scoped by room for targeted updates. This design does not guarantee durable event delivery if the process stops immediately after commit; an outbox can be added if the production reliability requirements justify it.
- Administrators create time slots explicitly. Recurring calendar rules are not implemented.
- EF Core InMemory does not prove SQL Server uniqueness behavior; concurrency verification always uses SQL Server through Testcontainers.
- Azure deployment and a public demo URL remain unverified until the workflow is run in a configured subscription.

## 17. Critical acceptance demo

1. Start the application and authenticate two browser sessions.
2. Open the same room schedule in both sessions.
3. Attempt to book one available slot from both sessions at nearly the same time.
4. Verify one success, one HTTP 409, one database booking, and a live SignalR schedule update.
5. Run the concurrency integration test to verify the invariant against SQL Server.
