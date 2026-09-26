# Roomly User and Administrator Guide

This guide explains the main workflows in Roomly. The application UI, this guide, and the AI booking guide are written in English.

## Open the application

- Main booking application: `/`
- AI workspace and its extended Help page: `/index.html`
- Interactive API reference in Development: `/swagger`
- OpenAPI document in Development: `/openapi/v1.json`

Sign in with your account or create one from the sign-in page. The server assigns the `User` role to new accounts. Administrator capabilities are enforced by the API and are not granted by hiding or showing UI controls alone.

## Book a room

1. Open **Rooms & bookings**.
2. Choose an active room and a date.
3. Select **Book** beside an **Available** slot.
4. Confirm the success message. The booking then appears in **My bookings**.

The browser displays slot times in the local time zone. A slot marked **Booked** cannot be selected. A booking belongs to the authenticated user; the client cannot choose another user's identity.

### Simultaneous booking attempts

The database enforces at most one booking per time slot with a unique index. If another request reserves the same slot first, Roomly returns a clear conflict and refreshes the schedule. This is an expected outcome, not a server failure. Open schedules receive `SlotBooked` and `SlotCancelled` updates through SignalR after the database change commits.

## Manage your bookings and profile

- **My bookings** lists the current user's reservations. You can cancel your own booking before its slot starts. A cancellation frees the slot and updates viewers in real time.
- **My profile** lets you update the display name and phone number, change your password, and upload or remove an avatar.
- Profile and room image uploads accept JPEG, PNG, or WebP files up to 10 MB. Room cards use the default room image when no photo is available; profile pictures use the default avatar.

## Directory and quick search

The header's quick search and **Directory** search find people and rooms. Search is debounced and combines live database results with the local JSON fixture data under `wwwroot/data/`. Matching fixture and database entries are merged to avoid duplicate results. Directory results are paginated and images are lazy-loaded; missing images use placeholders.

The fixture is demonstration data, not an identity provider or a source of booking authority. A person appearing in the directory does not grant permission to act as that person.

## AI-assisted booking

Open **AI workspace** and sign in. A direct request can book a slot when it clearly expresses booking intent and identifies an exact room, date, and start time. Example:

> Book Cedar on September 29 at 10:00 AM.

The assistant interprets time in the browser's time zone unless another zone is specified. If the request is ambiguous, it asks for clarification instead of choosing a nearby slot. Availability questions produce suggestions only; review the proposal and choose **Confirm booking** to reserve it. A successful AI booking is labeled **Booked by AI**. AI cannot cancel a booking.

The AI provider is optional and configured on the server. Manual room browsing and booking continue to work when the provider is unavailable. Do not put API keys in browser code or client-side configuration. See the [AI-assisted booking guide](ai-assisted-booking.md) for provider configuration, tool guardrails, exact-slot validation, and test commands.

## Administrator workflows

Accounts with the `Admin` role see the **Admin** view and room-management actions. Administrators can:

- Add, edit, activate/deactivate, and delete rooms.
- Upload room images (JPEG, PNG, or WebP, up to 10 MB).
- Add fixed availability slots to a selected room.
- Review all bookings and the system overview.

Choose valid, non-overlapping availability and use UTC-safe timestamps in integrations. Existing bookings remain subject to the database's unique slot constraint. The admin UI is a convenience; authorization and validation are repeated by the server.

## Troubleshooting

| Symptom | What to check |
|---|---|
| Sign-in fails | Confirm the email/password and that the account exists. New accounts receive the User role. |
| A slot became unavailable | Another user may have booked it. Select another slot; the schedule refreshes after conflicts and realtime events. |
| Live updates show reconnecting | Check network access to the application and SignalR endpoint, then wait for automatic reconnect or reload the schedule. Manual booking remains available. |
| AI assistant is unavailable | The feature needs a server-side provider key and provider connectivity. Use manual booking while it is unavailable. |
| An image is rejected | Use JPEG, PNG, or WebP and keep the file at or below 10 MB. |
| Admin controls are missing | The signed-in account needs the Admin role. UI visibility is role-based and the API independently checks authorization. |

## Local development and documentation

Start the server from the repository root:

```bash
dotnet run --project src/Server/RoomBooking.Server.csproj
```

The local Development profile exposes Swagger/OpenAPI. Additional repository documentation:

- [Architecture and C4 diagrams](architecture/README.md)
- [AI-assisted booking guide](ai-assisted-booking.md)
- [Booking concurrency decision](architecture/../adr/001-concurrency-strategy.md)
- [Meeting room system specification](specs/meeting-room-system.md)

For implementation, migrations, tests, and deployment commands, use the repository [README](../README.md) and [CLAUDE.md](CLAUDE.md).
