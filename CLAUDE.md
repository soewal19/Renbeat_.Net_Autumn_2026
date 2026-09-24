# Repository instructions

Follow the project guidance in [docs/CLAUDE.md](docs/CLAUDE.md) for architecture, coding rules, concurrency invariants, security requirements, and acceptance checks.

The booking uniqueness constraint in the database is mandatory. Do not replace it with a pre-check or application lock. Keep the browser app same-origin with the API and publish SignalR booking events only after the database save succeeds.

## Optional AI, Skills, Help, OpenAPI and architecture documentation

- Preserve the existing booking path and database unique index. AI is optional and must never become a dependency of booking correctness or application startup.
- The checked-in frontend is same-origin HTML/CSS/JavaScript under `src/Server/wwwroot`. Do not describe or assume a Blazor WebAssembly client unless one is actually added.
- `IAiAssistant` is the provider boundary. Groq credentials stay server-side in environment/configuration (`GROQ_API_KEY`); never place them in Razor, JavaScript, static assets, or browser configuration. Model/endpoint can be configured (`GROQ_MODEL`, `GROQ_ENDPOINT`).
- AI tools are a fixed allowlist and read-only. They call `IAiToolService`, receive the authenticated user ID from the server, and never accept arbitrary code, SQL, URLs, or discovered tools. Do not add an AI booking tool unless it uses the unchanged BookingService/API path and the database uniqueness constraint.
- Skills are untrusted text guidance, not executable code. Only admins manage them. AI-generated/uploaded content must validate as a `SkillDefinition`, remain an editable unsaved draft first, save inactive, and require an explicit admin activation. Upload only `.md`, `.txt`, `.json`, validate UTF-8 and enforce the 64 KB cap; never execute uploads.
- A Groq failure or missing key must produce a safe AI-unavailable response while core auth, rooms, schedules, bookings, concurrency and SignalR continue working. Never expose provider errors/secrets or log prompts/passwords/tokens.
- Keep OpenAPI summaries, auth/role information, request validation and explicit response codes current. The booking API documents 201/400/401/403/404/409. Swagger UI is Development-only.
- Keep Mermaid C4 diagrams in `docs/architecture/` accurate to actual deployed code. Update README and both CLAUDE.md files when architecture/configuration changes.
- Help content must explain booking, conflict behavior, SignalR, roles, optional AI and Skills; Swagger links reflect its Development-only availability.
- Run the focused AI tests, complete Release build and full tests before claiming completion. SQL integration/concurrency tests require Docker; report unavailable environment checks honestly.
