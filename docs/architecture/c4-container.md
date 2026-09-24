# C4 — Containers (C2)

```mermaid
flowchart TB
    browser[Browser\nHTML, CSS, JavaScript\nCookie-authenticated same-origin UI]
    web[ASP.NET Core .NET 10\nAPI, Identity, OpenAPI, SignalR hub]
    booking[Booking and schedule endpoints\nExisting core]
    ai[Optional AI feature\nAssistant, allowlisted tools, Skills]
    sql[(Azure SQL\nIdentity, room, schedule, booking, skill data)]
    signalr[Azure SignalR]
    groq[Groq chat completions API]
    browser -->|HTTP and SignalR client| web
    web --> booking
    web --> ai
    booking --> sql
    booking -->|after successful database save| signalr
    ai -->|read-only application tool service| sql
    ai -->|optional server-side API key| groq
```

The checked-in repository currently serves a static same-origin browser application from `src/Server/wwwroot`; it does **not** contain a Blazor Web App or Interactive WebAssembly client. This diagram describes the implementation that exists. No parallel Blazor application is represented as shipped functionality.
