# Repository instructions

This repository is a .NET 10 modular monolith. Read [docs/CLAUDE.md](docs/CLAUDE.md) for detailed requirements, commands, security guidance, and review checks.

## Architecture

- ASP.NET Core host and API: `src/Server/`.
- Primary UI: same-origin Blazor Web App with Interactive WebAssembly in `src/Client/RoomBooking.Client/`, hosted by `src/Server/Components/`.
- Shared HTTP contracts: `src/Shared/`; typed browser clients live in the Blazor client services.
- API features and endpoint mappings: `src/Server/Features/`.
- EF Core, Identity, and SignalR infrastructure: `src/Server/Infrastructure/`.
- Azure resources are described in `infra/`; CI and deployment workflows are in `.github/workflows/`.
- There is one application UI. Keep AI Assistant, Skills, Help, analytics, and booking workflows in Blazor. Do not reintroduce a second static application under `wwwroot`.

## Booking invariant

`BookingService` is the single application service used by both the regular booking endpoint and AI `book_slot`. The database unique index on `Booking.TimeSlotId` is the final authority. Never replace it with a check-then-insert, a C# lock, or frontend validation. Map duplicate-key violations to a normal conflict result, not HTTP 500. Publish `SlotBooked` only after persistence succeeds.

## AI and security

- AI is optional and must not be required for startup or ordinary booking. `IAiAssistant` is the server-side provider boundary; Groq credentials stay in User Secrets, Key Vault, or environment configuration.
- Expose only allowlisted tools. `book_slot` is enabled only for explicit booking intent and must call `BookingService` with a server-derived user ID and an exact, validated future slot.
- Availability proposals are read-only until the user confirms. The AI cannot cancel bookings or access arbitrary SQL, code, files, URLs, or tools.
- Skills are untrusted text guidance. Validate UTF-8 uploads (`.md`, `.txt`, `.json`, maximum 64 KB); save drafts inactive and require explicit administrator activation. Never execute skill content.
- Never commit passwords, tokens, connection strings, local `appsettings.Development.json`, or account lists. Production and staging must fail startup when Azure SQL configuration is missing.
- Never return provider/database exception details to clients or log credentials, prompts, or tokens.

## Realtime, migrations, and tests

- Booking commands use HTTP. SignalR is for authorized resource schedule groups and notifications after successful persistence only.
- Create a new EF migration for every model change. Never change a migration already applied to a shared database. If an unapplied historical migration contains schema SQL that the target database cannot execute, correct it before deployment and document the reason. Do not suppress `PendingModelChangesWarning` to hide snapshot drift.
- Run `dotnet build RoomBooking.slnx --configuration Release` and `dotnet test` before declaring a change verified. Integration and concurrency tests use SQL Server Testcontainers and require Docker.
- Update the README, user/AI guides, C4 diagrams, ADRs, and this file when behavior or architecture changes.