# Repository instructions

Follow the project guidance in [docs/CLAUDE.md](docs/CLAUDE.md) for architecture, coding rules, concurrency invariants, security requirements, and acceptance checks.

The booking uniqueness constraint in the database is mandatory. Do not replace it with a pre-check or application lock. Keep the browser app same-origin with the API and publish SignalR booking events only after the database save succeeds.
