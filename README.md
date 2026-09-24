# Reenbit Meeting Room Booking System

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
| Просмотр всех бронирований системы | ❌ | ✅ |

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
- `tests/UnitTests/` — валидаторы, хелперы
- `tests/IntegrationTests/` — API + auth (WebApplicationFactory + Testcontainers SQL Server)
- `tests/ConcurrencyTests/` — **обязательный** тест конкурентности, доказывающий инвариант

---

## 4. Технологический стек

| Область | Технологии |
|---|---|
| Backend | .NET 10, ASP.NET Core, Minimal APIs, ProblemDetails |
| ORM / DB | Entity Framework Core, SQL Server provider, Migrations |
| Auth | ASP.NET Core Identity (IdentityDbContext), cookie auth, Roles |
| Real-time | ASP.NET Core SignalR → Azure SignalR Service |
| API docs | Swagger / OpenAPI (Swashbuckle) |
| Validation | FluentValidation |
| Tests | xUnit, FluentAssertions, WebApplicationFactory, Testcontainers.MsSql |
| Infra as Code | Bicep (`infra/`) |
| CI/CD | GitHub Actions (`.github/workflows/ci.yml`) |
| Observability | Application Insights (в Bicep) |

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

Приложение с пользовательским интерфейсом поднимется на `https://localhost:7274/`; Swagger UI доступен на `https://localhost:7274/swagger`.

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
| `ASPNETCORE_ENVIRONMENT` | `Development` / `Staging` / `Production`. |
| `APPLICATIONINSIGHTS_CONNECTION_STRING` | Application Insights. |

Никакие секреты не коммитятся. В проде рекомендуется **Managed Identity** + **Azure Key Vault**.

---

## 13. Развёртывание в Azure

Целевая схема (один origin):

```
Azure App Service
  ├─ ASP.NET Core Minimal API
  └─ Static HTML/CSS/JavaScript frontend (served by ASP.NET Core)

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

The App Service stores the Data Protection key ring under its persistent shared `/home` storage so authentication cookies work across instances and package deployments. Forwarded headers are enabled so HTTPS redirection recognizes the original client scheme behind the App Service proxy.

To deploy from GitHub Actions, push the repository and configure these repository secrets: `AZURE_CREDENTIALS`, `AZURE_SUBSCRIPTION_ID`, `SQL_ADMIN_PASSWORD`, and `APP_ADMIN_PASSWORD`. The Azure identity needs subscription deployment permissions, including permission to create role assignments for the App Service managed identity. Run **CI - Build & Test** with `workflow_dispatch`; the deploy job runs only after all required tests pass, provisions the Bicep resources, deploys the published package, and prints the app URL in the workflow summary.

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
- Frontend is a compact same-origin HTML/CSS/JavaScript app served from `src/Server/wwwroot`; SignalR's browser client is loaded from jsDelivr.
- InMemory-провайдер EF Core **не гарантирует** UNIQUE так же, как SQL Server; интеграционные и concurrency тесты всегда ходят в настоящий SQL через Testcontainers.
- The Azure deployment workflow requires the repository secrets listed above. No public demo URL is available until it is run in a configured Azure subscription.

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
