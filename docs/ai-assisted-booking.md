# AI-Assisted Room Booking

This guide describes how to use and operate the optional Roomly AI booking feature.

## Where to find it

The application is a single Blazor WebAssembly interface at the root. Open the **AI booking** tab to use the chat. It uses the same authenticated API, booking service, database constraints, and SignalR hub. AI chat requires a signed-in account.

## Two booking modes

### Direct booking command

The assistant can book a room without a separate confirmation when the user clearly requests a booking and supplies enough detail to identify an exact slot. For example:

> Book Cedar on September 29 at 10:00 AM.

The browser sends its time-zone ID with the chat request. Unless the user specifies another zone, the assistant interprets the requested date and time in that browser zone. The chat endpoint is stateless, so if the assistant asks a follow-up question, send a new message containing the full booking command and all required details.

The assistant must ask a follow-up instead of guessing when the room, date, or start time is unclear. A server-side intent detector must first recognize an explicit, non-negated booking command before the `book_slot` tool is made available. Availability questions, hypothetical questions, and negated commands must not create a booking.

### Availability suggestion

For a question such as “Which rooms are available tomorrow?”, the assistant can check live availability and return a proposal. This path is read-only until the user selects **Confirm booking**. The UI displays a success toast and refreshes the schedule and booking lists after confirmation.

## Server-side booking checks

For autonomous booking, the model supplies a resource ID, local date, and local start time. The server:

1. Validates the request and resolves the supplied time zone.
2. Converts the local date and start time to UTC.
3. Requires an exact match for an active room's future time slot. It does not silently select a nearby time.
4. Attempts the booking insert with the authenticated user's server-derived ID and marks the record as AI-generated.
5. Relies on the unique database index `IX_Bookings_TimeSlotId_Unique` as the final guard against competing requests.
6. Publishes `SlotBooked` only after the database save succeeds.

If the slot is missing, in the past, inactive, already taken, or ambiguous because of a daylight-saving transition, the system does not create a booking. A competing booking is reported as a conflict; the AI must not claim that it succeeded. A successful AI-created booking is labeled **Booked by AI** in the user's and administrator's booking lists. The ordinary booking endpoint does not accept a client-supplied AI source flag.

## Cancellation

Users can cancel their own future bookings from **My Bookings**. The API operation is `DELETE /api/bookings/{id}`. It returns no booking details if the ID does not belong to the user or the slot has already started. Once deletion commits, the server publishes `SlotCancelled`; viewers of that room's schedule refresh their availability.

The AI chat does not cancel bookings. Users must use the cancellation action in the application.

## Provider configuration and availability

The feature uses Groq from the server. Configure `Groq:ApiKey` with .NET user secrets for local development, or `GROQ_API_KEY` through the deployment's secret configuration. Never place provider credentials in `appsettings*.json`, browser code, or public client configuration. Optional model settings are `GROQ_MODEL`, `GROQ_FALLBACK_MODEL`, `GROQ_ENDPOINT`, and `GROQ_TIMEOUT_SECONDS`.

If the provider is unavailable or not configured, the AI endpoint returns a safe unavailable response. Manual room browsing, booking, cancellation, concurrency protection, and realtime updates continue to work.

## Verification

Unit tests cover explicit and negated booking intent, tool gating, exact room/date/time matching, and AI booking attribution:

```bash
dotnet test tests/UnitTests/RoomBooking.UnitTests.csproj
```

The SQL Server concurrency integration test remains the proof that competing booking inserts cannot create duplicate bookings. It can be run with:

```bash
dotnet test tests/ConcurrencyTests/RoomBooking.ConcurrencyTests.csproj
```

The integration and concurrency projects use SQL Server Testcontainers and require Docker.
