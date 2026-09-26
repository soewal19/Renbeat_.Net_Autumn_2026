# Reenbit Meeting Room Booking System

## English documentation

- [User and administrator guide](docs/user-guide.md) — booking, profiles, directory, roles, troubleshooting, and AI usage.
- [AI-assisted booking guide](docs/ai-assisted-booking.md) — explicit booking behavior, safeguards, provider configuration, and verification.
- [C4 architecture documentation](docs/architecture/README.md) — system context, containers, components, and deployment.
- [Concurrency decision record](docs/adr/001-concurrency-strategy.md) — why the database unique constraint guarantees one booking per slot.

For local development, store the seed administrator password with `dotnet user-secrets set "Seed:AdminPassword" "<strong-local-password>" --project src/Server/RoomBooking.Server.csproj`. Keep local credentials in User Secrets or environment variables; do not commit them.


Reenbit тестовое задание: система бронирования переговорных комнат с **гарантированным контролем конкурентности** и **реал-тайм обновлениями** через Azure SignalR.

---

## 1. Обзор проекта

Модульный монолит на **ASP.NET Core (.NET 10)** + **SQL Server** + **SignalR**, который:

- Управляет фиксированными bookable time-slots для ресурсов (переговорных).
- **Гарантирует отсутствие двойных бронирований** при любом параллелизме запросов (включая несколько инстансов App Service).
- Распространяет изменения статуса бронирований на всех клиентов, просматривающих расписание, без обновления страницы.
- Использует ролевую авторизацию ASP.NET Core Identity (`User`, `Admin`).

---

## 2. Функциональные требования

| Возможность | User | Admin |
|---|:---:|:---:|
| Просмотр списка ресурсов | ✅ | ✅ |
| Просмотр расписания ресурса | ✅ | ✅ |
| Бронирование свободного time-slot | ✅ | ✅ |
| Просмотр своих бронирований | ✅ | ✅ |
| Создание / редактирование / удаление ресурсов | ❌ | ✅ |
| Создание time-slots для ресурса | ❌ | ✅ |
| Загрузка и замена фотографии комнаты (JPEG, PNG, WebP до 10 МБ) | ❌ | ✅ |
| Просмотр всех бронирований системы | ❌ | ✅ |
| Аналитика бронирований и загрузки комнат | ❌ | ✅ |

---

## 3. Архитектура

### High-level

```
┌──────────────────────────────────────────────────────┐
│                Azure App Service (One origin)        │
│                                                      │
│  ┌──────────────┐  ┌──────────────┐  ┌────────────┐ │
│  │   API Layer  │  │   Identity   │  │  SignalR   │ │
│  │ (Minimal API)│  │ ASP.NET Core │  │   Hubs     │ │
│  └──────┬───────┘  └──────┬───────┘  └──────┬─────┘ │
│         │                 │                  │       │
│  ┌──────▼─────────────────▼──────────────────▼─────┐ │
│  │             Entity Framework Core                │ │
│  │         DbContext + Unique Indexes               │ │
│  └───────────────────────┬─────────────────────────┘ │
└──────────────────────────┼───────────────────────────┘
                           │
              ┌────────────▼──────────────┐
              │     Azure SQL Database    │
              │  UNIQUE(Booking.TimeSlotId)│
              └───────────────────────────┘
```

### Слои (Modular Monolith, Vertical Slice)

- `src/Server/` — backend (ASP.NET Core)
  - `Infrastructure/Persistence/` — EF Core DbContext, Entity Configurations, Entities
  - `Infrastructure/Identity/` — роли, seeder
  - `Infrastructure/SignalR/` — `ScheduleHub`
  - `Features` — реализованы inline в `Program.cs` via MapGroup endpoints (Auth / Resources / Schedule / Bookings / Admin)
- `src/Shared/` — DTO, SignalR message contracts
- `src/Client/RoomBooking.Client/` — Blazor WebAssembly UI, typed HTTP/SignalR clients
- `tests/UnitTests/` — валидаторы, хелперы
- `tests/IntegrationTests/` — API + auth (WebApplicationFactory + Testcontainers SQL Server)
- `tests/ConcurrencyTests/` — **обязательный** тест конкурентности, доказывающий инвариант

---

## 4. Технологический стек

| Область | Технологии |
|---|---|
| Backend | .NET 10, ASP.NET Core, Minimal APIs, ProblemDetails |
| Frontend | Blazor Web App, Interactive WebAssembly, Razor components, typed C# client services |
| ORM / DB | Entity Framework Core, SQL Server provider, Migrations |
| Auth | ASP.NET Core Identity (IdentityDbContext), cookie auth, Roles |
| Real-time | ASP.NET Core SignalR → Azure SignalR Service |
| API docs | ASP.NET Core OpenAPI + Swagger UI (Development only) |
| Validation | FluentValidation |
| Tests | xUnit, FluentAssertions, WebApplicationFactory, Testcontainers.MsSql |
| Infra as Code | Bicep (`infra/`) |
| CI/CD | GitHub Actions (`.github/workflows/ci.yml`) |
| Observability | Application Insights + Azure Monitor OpenTelemetry distro (connection string based) |

---

## 5. Стратегия конкурентности

> **Главный инвариант: один TimeSlot = НЕ БОЛЕЕ одного Booking.**

### Что **запрещено**

- ❌ `SELECT → проверить свободно → INSERT`.
- ❌ `lock ()`, `Monitor`, статические синхронизаторы, in-memory мьютексы.
- ❌ Любые предположения о single-instance hosting.

### Реализация

**Единственный источник истины — база данных.** На `Booking.TimeSlotId` объявлен **UNIQUE индекс**:

```csharp
// BookingConfiguration.cs
builder.HasIndex(b => b.TimeSlotId)
       .IsUnique()
       .HasDatabaseName("IX_Bookings_TimeSlotId_Unique");
```

Алгоритм бронирования:

```
POST /api/bookings
   │
   ├─ Валидация запроса
   ├─ Добавление Booking в DbContext
   ├─ SaveChangesAsync()
   │     │
   │     ├─ Успех → 201 Created → SignalR SlotBooked (ПОСЛЕ коммита!)
   │     └─ DbUpdateException with SQL 2601/2627
   │          → поймали через exception filter
   │          → HTTP 409 Conflict
   │          → ProblemDetails { code: SLOT_ALREADY_BOOKED }
```

**Порядок критичен**: SignalR уведомление посылается **только после** того как `SaveChangesAsync` подтвердил запись. Никогда не уведомляем клиентов "авансом".

Полное объяснение см. [ADR 001](docs/adr/001-concurrency-strategy.md).

---

## 6. Почему двойное бронирование невозможно

Даже при запуске N инстансов Azure App Service:

1. Любой `INSERT` в `Bookings` в любом из инстансов доходит до одного и того же SQL Server.
2. SQL атомарно проверяет уникальность по индексу.
3. Первый запрос, дошедший до листовой страницы индекса — проходит.
4. Все остальные — SQL Error 2601/2627 → EF оборачивает в `DbUpdateException` → приложение возвращает 409.

Количество бронирований в базе никогда не превысит 1. Поведение наблюдается и проверяется автоматическим concurrency integration test на настоящем SQL Server.

---

## 7. Аутентификация и роли

Используется ASP.NET Core Identity поверх `IdentityDbContext<ApplicationUser>`.

- `User` — default роль, присваивается при регистрации.
- `Admin` — seedится при старте (email/password из `Seed:AdminEmail` / `Seed:AdminPassword` в конфигурации).

Все политики авторизации проверяются **на сервере** (endpoints помечены `[Authorize]` и `[RequireAuthorization(AppRoles.Admin)]`). Сокрытие кнопок в UI никогда не является единственным механизмом защиты.

---

## 8. SignalR архитектура

Hub: `/hubs/schedule` (требует `[Authorize]`).

- Клиент открыл расписание ресурса `N` → вызывает `JoinResource(N)` → добавляется в группу `resource:{N}`.
- Клиент ушёл → `LeaveResource(N)`.
- После успешного подтверждённого бронирования сервер вызывает:
  ```
  Clients.Group("resource:{resourceId}").SendAsync("SlotBooked", evt)
  ```
  где `evt = { slotId, resourceId, bookingId, bookedAtUtc }`. Public schedule updates omit the booker's identity; administrator booking reports contain user details.
- Все клиенты, наблюдающие это расписание, получают событие и обновляют UI **без перезагрузки страницы**.

Константы имён методов и контракты событий лежат в [Shared/SignalR](src/Shared/SignalR).

---

## 9. Стратегия тестирования

- **Unit** → `FluentValidation` валидаторы DTO, `DbConcurrencyHelper`.
- **Integration** → `WebApplicationFactory<Program>` + real SQL Server через `Testcontainers.MsSql`:
  - регистрация, логин, 401 на некорректном пароле, дубликат email → 400.
  - GET /api/resources анонимно → 200.
  - POST /api/resources анонимно → 401; админом → 201 + сущность в БД.
- **Concurrency** (ОБЯЗАТЕЛЬНЫЙ):
  - 20 аутентифицированных клиентов, один и тот же слот, синхронизированный вызов (TCS barrier).
  - Утверждения: `201 == 1`, `409 == 19`, `COUNT(Bookings WHERE TimeSlotId = X) == 1`.

Run:

```bash
dotnet test
```

---

## 10. Обязательный concurrency-тест

Код: [ConcurrencyBookingTests.cs](tests/ConcurrencyTests/ConcurrencyBookingTests.cs).

Ключевые моменты:

- Тест использует **настоящий** SQL Server в контейнере. Mock-репозитории не годятся — инвариант enforced не в коде, а в индексе.
- Каждый из N пользователей — отдельный `HttpClient` с отдельным cookie-контейнером.
- Все вызовы синхронизируются `TaskCompletionSource` + случайный джиттер 0..20 мс.
- В конце тест напрямую ходит в `AppDbContext` и делает `SELECT COUNT(*)` для доказательства.

---

## 11. Локальный запуск

### Предварительно
- .NET 10 SDK
- Docker (для integration/concurrency тестов; сам проект умеет работать через InMemory при отсутствии строки подключения)
- Опционально: LocalDB / SQL Server / Docker SQL для production-like режима.

### Minimal commands

```bash
dotnet restore
dotnet build
dotnet run --project src/Server/RoomBooking.Server.csproj
```

Приложение с пользовательским интерфейсом поднимется на `https://localhost:7274/`; в Development Swagger UI доступен на `https://localhost:7274/swagger`, OpenAPI JSON — `https://localhost:7274/openapi/v1.json`. Сначала вызовите `POST /api/auth/login` в Swagger UI: same-origin Identity cookie будет отправлена со следующими запросами.

### Default seed admin

- Email: `admin@reenbeat.com`
- Password: `Admin123!`
- Переопределяется через конфиг `Seed:AdminEmail` / `Seed:AdminPassword`.

### Тесты

```bash
dotnet test
```

Интеграционные и concurrency тесты автоматически поднимут MS SQL контейнер через Testcontainers (требуется Docker).

---

## 12. Конфигурация / Переменные окружения

| Ключ | Описание |
|---|---|
| `ConnectionStrings:DefaultConnection` | SQL Server. Если пусто → InMemory (fallback для простого `dotnet run`). |
| `ConnectionStrings:AzureSignalR` | Connection string Azure SignalR Service. Если пусто → встроенный SignalR (локальная разработка). |
| `Seed:AdminEmail` / `Seed:AdminPassword` | Учётка seed-админа при старте. |
| `Seed:DemoData` | Добавляет демонстрационные записи комнат, слотов и пользователей из `wwwroot/data/directory.json`; по умолчанию выключено. Demo identities создаются без пароля и не могут войти. |
| `ASPNETCORE_ENVIRONMENT` | `Development` / `Staging` / `Production`. |
| `APPLICATIONINSIGHTS_CONNECTION_STRING` | Enables Azure Monitor OpenTelemetry export to Application Insights. |

Фотографии комнат хранятся в базе данных вместе с проверенным MIME-типом; API отдаёт их через `GET /api/resources/{id}/image`. Если фото не загружено, интерфейс показывает `wwwroot/images/rooms/no_image_rooms.png`. Загрузка доступна только Admin через `POST /api/resources/{id}/image`.

Никакие секреты не коммитятся. В проде рекомендуется **Managed Identity** + **Azure Key Vault**.

---

## 13. Развёртывание в Azure

Целевая схема (один origin):

```
Azure App Service
  ├─ ASP.NET Core Minimal API
  ├─ Blazor Web App / Interactive WebAssembly client
  └─ Legacy static AI workspace at /index.html

Зависимости:
  ├─ Azure SQL Database (credentials supplied through Key Vault references)
  ├─ Azure SignalR Service
  ├─ Application Insights
  └─ Azure Key Vault (SQL, SignalR, and initial admin secrets)
```

---

## 14. Bicep infrastructure-as-code

Всё воспроизводимо: [infra/main.bicep](infra/main.bicep) + модули:

| Модуль | Назначение |
|---|---|
| `modules/appservice.bicep` | App Service Plan + Web App (.NET 10, managed identity, Application Insights) |
| `modules/sql.bicep` | Azure SQL Server + база, firewall rules, SQL credentials in Key Vault |
| `modules/signalr.bicep` | Azure SignalR Service (Serverless mode или Default) |
| `modules/monitoring.bicep` | Application Insights, workspace |
| `modules/keyvault.bicep` | Key Vault + managed-identity secret access |

Применение:

```bash
az login
az account set --subscription <sub-id>
az deployment sub create \
  --location westeurope \
  --template-file infra/main.bicep \
  --parameters environmentName=dev appName=roombooking \
    sqlAdminPassword='<strong-sql-password>' adminPassword='<strong-admin-password>'
```

The web app uses Key Vault references for SQL, Azure SignalR, and the initial admin password. Configure unique secure passwords for each environment; do not reuse the local Development seed credentials.

To deploy the sample workspace data to Azure SQL, opt in with the Bicep parameter `seedDemoData=true`. The app imports the versioned `wwwroot/data/directory.json` after migrations at startup, using the existing app-to-database connection. The import is idempotent, creates sample rooms and future time slots, and creates directory-only users without passwords. Leave the parameter false for a production workspace. Directory search combines this local JSON fixture with the authenticated `/api/directory/search` database endpoint and de-duplicates by email/name.

The App Service stores the Data Protection key ring under its persistent shared `/home` storage so authentication cookies work across instances and package deployments. Forwarded headers are enabled so HTTPS redirection recognizes the original client scheme behind the App Service proxy.

To deploy from GitHub Actions, push the repository and configure these repository secrets: `AZURE_CREDENTIALS`, `AZURE_SUBSCRIPTION_ID`, `SQL_ADMIN_PASSWORD`, and `APP_ADMIN_PASSWORD`. Optionally set `GROQ_API_KEY` to store the secret in Key Vault and expose it to App Service through a Key Vault reference; without it, only AI is unavailable. The Azure identity needs subscription deployment permissions, including permission to create role assignments for the App Service managed identity. Run **CI - Build & Test** with `workflow_dispatch`; the deploy job runs only after all required tests pass, provisions the Bicep resources, deploys the published package, and prints the app URL in the workflow summary.

---

## 15. CI/CD

Файл: [`.github/workflows/ci.yml`](.github/workflows/ci.yml). Запускается на push и PR:

| Stage | Делает |
|---|---|
| restore | `dotnet restore` |
| build | `dotnet build -c Release` |
| unit tests | `tests/UnitTests` |
| integration tests | `tests/IntegrationTests` (Docker required) |
| concurrency tests | `tests/ConcurrencyTests` (Docker required) |
| publish | подготовка и публикация артефакта |
| deploy | ручной `workflow_dispatch` в Azure после успешных тестов |

**Deployment запрещён при падении обязательных тестов**, включая concurrency.

---

## 16. Использование Claude Code

В корне расположен [CLAUDE.md](CLAUDE.md), который подключает подробный [гайдлайн проекта](docs/CLAUDE.md) для Claude Code:
- архитектура, инварианты,
- явно перечислено что делать и **что запрещено** (naive check→insert, in-memory locks, лишние абстракции),
- финальный Acceptance Checklist (пункт 27).

Любое изменение, затрагивающее инвариант конкурентности, должно:
1. пройти автоматический concurrency-тест;
2. обновить ADR и CLAUDE.md при смене механизма.

Спецификация и acceptance map ведутся в [docs/specs/meeting-room-system.md](docs/specs/meeting-room-system.md) по SDD-подходу: требования, решения, этапы и фактические доказательства выполнения.

---

## 17. Трэйд-оффсы

| Решение | За | Против |
|---|---|---|
| UNIQUE-индекс на `Booking.TimeSlotId` | Простейшая корректность при любом количестве инстансов. | Первый-chance exception при конфликте (нормальный путь EF Core для этой ситуации). |
| Modular Monolith | Обозреваемый код, единая сборка, одна точка деплоя. Требования таска не тянут микросервисы. | Если будет отдельный scalе auth/bookings — придётся резать. |
| Minimal API + endpoints inline | Обозреваемо, нет контроллерного бойлерплейта. | Если сильно разрастётся — вынести в `Features/` vertical slices. |
| SignalR group-per-resource | Прямолинейно и экономично. | Один hot resource → много трафика на один хаб. |
| Без outbox-паттерна сейчас | Таск тестовый, не оправдан. | Очень редко SignalR нотификация может не долететь (App Domain unload right after commit); в проде можно добавить outbox позднее. |

---

## 18. Известные ограничения

- Time-slots задаются админом явно; автоматическая генерация календаря по повторам "каждый понедельник 10:00" не реализована.
- Основной интерфейс — same-origin Blazor Web App с Interactive WebAssembly из `src/Client/RoomBooking.Client`; Razor-компоненты используют типизированные C# клиенты HTTP и SignalR. Старый статический AI workspace сохранён по адресу `/index.html`.
- InMemory-провайдер EF Core **не гарантирует** UNIQUE так же, как SQL Server; интеграционные и concurrency тесты всегда ходят в настоящий SQL через Testcontainers.
- The Azure deployment workflow requires the repository secrets listed above. No public demo URL is available until it is run in a configured Azure subscription.

`APPLICATIONINSIGHTS_CONNECTION_STRING` включает официальный Azure Monitor OpenTelemetry SDK для ASP.NET Core, исходящих HTTP/SQL зависимостей, метрик и структурированных логов. Локально без connection string остаётся стандартное логирование ASP.NET Core.

---

## 19. Demo / Как проверить критичные требования

1. **Запустить**: `dotnet run --project src/Server/RoomBooking.Server.csproj`.
2. **Swagger** → `https://localhost:7274/swagger`.
3. **Admin seed**:
   - `POST /api/auth/login` → `{ "email": "admin@reenbeat.com", "password": "Admin123!" }`.
4. **Создать ресурс**: `POST /api/resources`.
5. **Создать slot**: `POST /api/resources/{id}/slots` → `[{ "startUtc": "...", "endUtc": "..." }]`.
6. **Зарегистрировать 20 пользователей** и запустить concurrency-тест:
   ```bash
   dotnet test tests/ConcurrencyTests/RoomBooking.ConcurrencyTests.csproj --logger "console;verbosity=detailed"
   ```
   Ожидаемый итог: `1 x 201`, `19 x 409`, `COUNT == 1` в БД.
7. **SignalR**: подключиться к `/hubs/schedule` под identity cookie, `JoinResource(id)`, сделать бронирование из другого клиента → убедиться, что `SlotBooked` приходит без page refresh.

## Optional AI, Skills and Help

The optional server-side Groq integration provides live room and schedule answers and supports autonomous booking for a clear, explicit user command. Manual booking remains available when the AI provider is unavailable. The database unique index on `Booking.TimeSlotId` remains the final authority preventing double booking.

- The browser calls `POST /api/ai/chat`; the server calls Groq using a server-side secret and configurable primary/fallback models (`openai/gpt-oss-120b` → `openai/gpt-oss-20b`). The key is never returned to the browser.
- The assistant has an explicit tool allowlist: `get_resources`, `get_schedule`, `get_my_bookings`, `get_booking_policy`, `propose_booking`, and conditionally `book_slot`. Booking tools are exposed only when the message starts with an explicit booking instruction; negated instructions do not enable them. Tools cannot run SQL/code, access the filesystem, discover tools, or access arbitrary URLs.
- For autonomous booking, the assistant requires a clear room, local date, and start time. The browser sends its IANA time-zone ID; the server converts the requested local time to UTC and requires an exact match with an active room's future slot. Ambiguous daylight-saving times and missing or occupied slots are rejected. The AI tool inserts directly and relies on the same unique `Booking.TimeSlotId` index as the regular booking endpoint. A duplicate-key race is returned to the assistant as a normal conflict; it never creates a second booking or returns a successful receipt.
- Availability questions use `propose_booking`, which is read-only. The user must review and confirm its proposed slot. Directly requested bookings are marked `IsAiGenerated` and shown as **Booked by AI** in personal and admin booking lists. Ordinary booking requests cannot set this marker.
- Users can cancel their own future bookings with `DELETE /api/bookings/{id}` or the **Cancel booking** action in **My Bookings**. Past bookings and other users' bookings cannot be cancelled through this endpoint. A `SlotCancelled` SignalR event updates active schedule viewers after the delete commits. The AI assistant does not cancel bookings itself.
- Give the room, date, and start time in one direct request, for example: `Book Cedar on September 29 at 10:00 AM.` If a detail is unclear, the assistant asks rather than choosing for you. This chat endpoint is stateless; if it asks a follow-up, send a new complete booking command with all details.
- On HTTP 429, 5xx, network failure, or provider timeout, the server retries with the fallback model; invalid credentials and malformed requests are returned without retry. Failover also applies to AI skill drafts. Groq account-wide quota exhaustion cannot be bypassed by another model under the same account.
- Skills are plain text stored in the `AiSkills` table. Admins can create, edit, activate, deactivate and delete them. AI generation and `.md`/`.txt`/`.json` uploads return drafts; saving creates an inactive record, and activation is a separate admin action. Skills are supplemental untrusted text and cannot grant tools or override server authorization. Uploads are UTF-8 validated, limited to 64 KB and never written to disk or executed.
- If the Groq key is missing, the AI endpoints return 503 and the UI states that AI is unavailable. Room search, manual booking, conflict control, cancellation and SignalR continue to work.
- The **Help** tab explains direct AI booking, suggestions, cancellations, conflicts, SignalR, roles and provider availability. Swagger UI is available at `/swagger` in Development only.
- The complete English user and administrator guide is [`docs/user-guide.md`](docs/user-guide.md); AI-specific operating details are in [`docs/ai-assisted-booking.md`](docs/ai-assisted-booking.md).
- The main `/` application is Blazor WebAssembly; the retained static AI workspace is available at `/index.html`. Both use the same authenticated API, booking records and SignalR hub. C4 diagrams live in [`docs/architecture/`](docs/architecture/): context, container, component and deployment.

Local optional AI configuration (never commit the key):

```powershell
dotnet user-secrets init --project src/Server/RoomBooking.Server.csproj
dotnet user-secrets set "Groq:ApiKey" "<your server-side Groq key>" --project src/Server/RoomBooking.Server.csproj
dotnet user-secrets set "Groq:Model" "openai/gpt-oss-120b" --project src/Server/RoomBooking.Server.csproj
dotnet user-secrets set "Groq:FallbackModel" "openai/gpt-oss-20b" --project src/Server/RoomBooking.Server.csproj
dotnet run --project src/Server/RoomBooking.Server.csproj
```

For Azure, configure the optional `GROQ_API_KEY` GitHub secret before deployment; Bicep stores it in Key Vault and App Service receives a Key Vault reference. `GROQ_MODEL` and `GROQ_FALLBACK_MODEL` default in App Service configuration and can be changed there. Do not put the key in `appsettings*.json`, `wwwroot`, JavaScript, or browser-visible configuration. Deployment without the optional key is supported. The AI endpoints are limited to 15 requests per minute per user/IP per app instance; this is abuse protection, not a globally coordinated quota across scaled instances.

Model failover can recover from a model-specific limit or temporary model/provider outage. Groq applies some rate limits at the organization level, so a fallback model using the same API key cannot bypass an exhausted organization-wide token/request quota. A separate provider and its own credentials would be needed for cross-provider failover.

### AI checks

```bash
dotnet test tests/UnitTests/RoomBooking.UnitTests.csproj --configuration Release
dotnet test tests/IntegrationTests/RoomBooking.IntegrationTests.csproj --configuration Release
```

Unit coverage includes explicit/negated booking intent, autonomous tool gating, exact slot matching, AI-source attribution, upload validation, invalid/missing provider behavior, user-scoped booking reads, and draft Skill lifecycle. Integration coverage checks authentication and admin-only Skill management. Integration/concurrency tests require Docker for SQL Server Testcontainers.
