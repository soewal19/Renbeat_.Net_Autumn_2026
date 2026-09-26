# Meeting Room Booking System — Specification and Delivery Plan

## Purpose

Provide a reviewable, runnable meeting-room booking system whose database prevents double booking under concurrent requests. This document is the SDD source of truth: requirements first, implementation decisions second, and verification evidence last.

## Acceptance criteria

| ID | Requirement | Current implementation | Acceptance evidence |
|---|---|---|---|
| AUTH-01 | Identity registration, login, logout, session and User/Admin roles | Implemented in ASP.NET Core Identity | Auth integration tests; role authorization tests |
| RES-01 | Admin resource management; users can browse resources and upload room photos | Admin uploads validated JPEG/PNG/WebP images (5 MB max) stored in SQL; rooms without an image use `no_image_rooms.png` | Development API smoke test created a room with fallback, uploaded a PNG, fetched it, and deleted the temporary room |
| SCH-01 | Fixed UTC slots, visible availability, authenticated resource-group subscriptions | Implemented with EF Core and SignalR | API tests and two-client realtime check |
| BOOK-01 | At most one booking per slot, enforced by SQL unique index | Implemented as `IX_Bookings_TimeSlotId_Unique` | 20-client SQL Server concurrency test: 1×201, 19×409, one row |
| BOOK-02 | SignalR event is published only after persistence succeeds | Implemented in booking endpoint | Code review and integration behavior |
| UI-01 | Login, registration, schedule, bookings, profile, directory and admin UI | Implemented in Blazor Web App with Interactive WebAssembly and typed C# API/SignalR clients | `dotnet build RoomBooking.slnx`; browser walkthrough still recommended |
| SEARCH-01 | Search database records and local JSON fixtures, de-duplicating results | Implemented as authenticated API plus local JSON client merge | Search unit/integration checks and UI verification |
| SEED-01 | Optional repeatable Azure demo seed for users, rooms and slots | Implemented from `wwwroot/data/directory.json`, opt-in via Bicep/GitHub Actions | Repeated startup does not create duplicates; demo users have no password |
| AZ-01 | App Service, Azure SQL, Azure SignalR, Key Vault and monitoring through Bicep | Templates and workflow exist; no Azure account/remote is configured in this workspace | Bicep validation; deployment requires configured Azure credentials |
| OBS-01 | Structured logs and Application Insights telemetry | Structured booking diagnostics; Azure Monitor OpenTelemetry distro conditionally configured using `APPLICATIONINSIGHTS_CONNECTION_STRING` | Server build and Azure configuration review |
| DOC-01 | Architecture, concurrency ADR, Claude guidance, commands and deployment docs | README, CLAUDE.md, ADR and indexed C1–C4 Mermaid diagrams are aligned; OpenAPI summaries and response codes added | Build plus Development Swagger/OpenAPI smoke check |
| GIT-01 | Coherent atomic commits and repository submission link | Existing commit history is present; this checkout has no Git remote | `git log` and configured remote |

## Invariants and security boundaries

1. `Booking.TimeSlotId` has a database UNIQUE constraint. It is the only authority for booking uniqueness.
2. Booking is a direct insert attempt; no unprotected availability check decides success.
3. SQL duplicate-key errors are mapped to a safe HTTP 409 ProblemDetails response.
4. The booking event is sent after `SaveChangesAsync` succeeds.
5. Role enforcement is server-side. Directory database search requires authentication.
6. Demo seed is opt-in, idempotent and uses the application SQL connection. Fixture identities are created without passwords.
7. Search merges local JSON with API results by normalized email or room name; the database match enriches the fixture record.

## Architecture decisions

The application is a modular monolith: ASP.NET Core Minimal API and a Blazor Web App with Interactive WebAssembly are hosted on one origin. Typed C# clients isolate API and SignalR access from Razor components. The older static AI workspace remains available at `/index.html` for existing demo workflows. Booking commands continue over HTTP and SQL uniqueness remains authoritative.

## Delivery sequence

1. Inspect implementation and record requirements, decisions and environment constraints in this SDD.
2. Preserve the existing API while implementing the Blazor WebAssembly client and typed API/SignalR services.
3. Add optional demo seeding and safe database-plus-JSON directory search.
4. Wire Application Insights through Azure Monitor OpenTelemetry; enable only when configured.
5. Build and run all tests; SQL-backed suites require a working Docker engine.
6. Audit Bicep and CI; deploy only with Azure credentials and publish URLs only when verified.

## Verification record

- `dotnet build RoomBooking.slnx --no-restore` — passed after Blazor integration, before adding the Azure Monitor package.
- `dotnet restore RoomBooking.slnx` — passed after retrieving the Azure Monitor dependency.
- `dotnet test RoomBooking.slnx --no-build --no-restore` — unit tests passed (38/38); SQL integration and concurrency suites could not start because Docker is unavailable in the environment. Re-run them with Docker before treating database acceptance as verified.
- Swagger/OpenAPI smoke check in Development — `/swagger` returned HTTP 200; `/openapi/v1.json` included RoomBooking API v1, booking/resource/schedule routes, and the documented 409 booking response.
- Azure deployment and public URL remain unverified because Azure CLI credentials are inaccessible to the workspace sandbox; the GitHub URL cannot be verified because this checkout has no Git remote.

## Current environment constraints

At the time of this audit, this checkout has no configured Git remote. Azure CLI credential state is inaccessible to the workspace sandbox, so deployment cannot be verified or safely performed here. These prevent producing a verifiable GitHub repository URL or Azure application URL; local implementation and validation remain available.
