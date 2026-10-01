# 🏥 Моніторинг пацієнтів у реальному часі

REST API + SignalR для відстеження стану пацієнтів у реальному часі.
Пристрої надсилають показники (пульс, тиск, SpO₂, тощо) через HTTP, а лікарі або дашборди підписуються на оновлення через SignalR.

## Зміст

- [Архітектура](#архітектура)
- [Стек технологій](#стек-технологій)
- [Вимоги](#вимоги)
- [Швидкий старт](#швидкий-старт)
- [Запуск Docker Compose](#запуск-docker-compose)
- [Тестування](#тестування)
- [API ендпоінти](#api-ендпоінти)
- [SignalR контракт](#signalr-контракт)
- [Налаштування порогів](#налаштування-порогів)
- [Структура проєкту](#структура-проєкту)

## Архітектура

```
┌──────────┐   POST /api/patients/{id}/vitals   ┌──────────────┐
│  Пристрій │──────────────────────────────────▶│  API (.NET)  │
│  / сенсор │                                   │  • Контролери│
└──────────┘                                   │  • Сервіси   │
                                               │  • EF Core   │
                                               └──────┬───────┘
                                                      │
                              ┌─────────────────────────┼──────────────────┐
                              │  PostgreSQL  │  SignalR Hub  │
                              │  (історія)   │  (реал-тайм)  │
                              └──────────────┴───────────────┘
                                              │
                              ┌───────────────┼───────────────┐
                              │  Браузер/      │  Моб. клієнт  │
                              │  Дашборд       │               │
                              └───────────────┴───────────────┘
```

1. **Пристрій** надсилає POST з показниками пацієнта.
2. **Валідація** на рівні `[ApiController]` та кастомної логіки (принаймні одне поле + діастолічний < систолічний).
3. **Сервіс `VitalSignsService`** записує в БД, оцінює стан через `VitalSignsAnalyzer`, надсилає SignalR-сповіщення.
4. **SignalR Hub** (`/hubs/monitoring`) розсилає `VitalSignsReceived` та `PatientAlert` підписаним клієнтам.
5. **Клієнт** підписується на конкретного пацієнта або на всіх пацієнтів.

## Стек технологій

| Компонент               | Технологія                                          |
|-------------------------|-----------------------------------------------------|
| Backend                 | ASP.NET Core 10 Web API + Controllers               |
| Real-time               | SignalR (JSON + str.enum)                           |
| БД                      | PostgreSQL 17 + EF Core + Npgsql                    |
| Міграції                | EF Core (автоматичний `Database.Migrate` на старті) |
| OpenAPI/документація    | .NET 10 Built-in OpenAPI + Scalar UI                |
| Докладання              | docker-compose                                      |
| Мова UI                 | 🇺🇦 Українська                                     |

## Вимоги

- .NET 10 SDK
- Docker Desktop (для PostgreSQL)
- npm не потрібен (SignalR JS client з CDN)

## Швидкий старт

### 1. Запустити PostgreSQL через Docker

```powershell
cd D:\dataS
docker compose up -d postgres
```

Переконайтесь, що контейнер запущений:

```powershell
docker compose ps
# healthcheck має бути "healthy"
```

### 2. Зробити міграцію БД

```powershell
cd src\PatientMonitoring.Api
dotnet ef migrations add InitialCreate
```

Це створить `Migrations/` папку з SQL-скриптами для PostgreSQL.

### 3. Запустити API

```powershell
dotnet watch run
# або
dotnet run
```

Сервіс буде доступний на `http://localhost:5225`.

### 4. Відкрити тестову сторінку

```
http://localhost:5225/
```

Це інтерактивний дашборд з:
- Списком пацієнтів у реальному часі (карточки з кольоровою індикацією стану)
- Формою створення нового пацієнта
- Імітатором пристрою для надсилання показників
- Логом SignalR-подій

### 5. Переглянути API-документацію

```
http://localhost:5225/scalar/v1
```

Можна тестувати ендпоінти прямо в браузері (вкладка Try it).

## Запуск Docker Compose

Згідно з `docker-compose.yml` у корені проєкту:

```powershell
docker compose up -d postgres
```

**Примітка:** API запускається окремо через `dotnet run`. Connection string в `appsettings.json` вказує на `Host=localhost`, тому Docker-Compose потрібен лише для БД.

Для запуску API всередині Docker з'єднання з Postgres буде через хост `postgres` — потрібно змінити connection string та додати Dockerfile. Це окрема тема розширення.

## Тестування з Postman / curl

### Створити пацієнта
```bash
curl -X POST http://localhost:5225/api/patients \
  -H "Content-Type: application/json" \
  -d '{"fullName":"Петренко Петро Петрович","ward":"Палата 3","bed":"2А"}'
```

### Записати показники (імітуємо пристрій)
```bash
curl -X POST http://localhost:5225/api/patients/{patientId}/vitals \
  -H "Content-Type: application/json" \
  -d '{
    "deviceId": "sensor-001",
    "heartRate": 118,
    "systolicBloodPressure": 145,
    "diastolicBloodPressure": 95,
    "oxygenSaturation": 91.5,
    "temperature": 38.4,
    "respiratoryRate": 22
  }'
```

### Отримати останні показники
```bash
curl http://localhost:5225/api/patients/{patientId}/vitals/latest
```

### Отримати історію (останні 50)
```bash
curl "http://localhost:5225/api/patients/{patientId}/vitals?limit=50"
```

### Список пацієнтів
```bash
curl http://localhost:5225/api/patients
```

## API ендпоінти

### Пацієнти

| Метод | Шлях | Опис |
|-------|------|------|
| `GET` | `/api/patients` | Список (за замовчуванням активні). Query: `includeInactive=true` |
| `GET` | `/api/patients/{id}` | Деталь з останніми показниками |
| `POST` | `/api/patients` | Створення. Body: `CreatePatientRequest` |
| `PUT` | `/api/patients/{id}` | Оновлення. Body: `UpdatePatientRequest` |
| `DELETE` | `/api/patients/{id}` | Деактивація (виписка). Історія залишається |

### Показники

| Метод | Шлях | Опис |
|-------|------|------|
| `GET` | `/api/patients/{id}/vitals/latest` | Останній показник (з кешуванням) |
| `POST` | `/api/patients/{id}/vitals` | Запис нових показників |
| `POST` | `/api/patients/{id}/vitals/batch` | Пакетне надсилання показників (офлайн-синхронізація) |
| `GET` | `/api/patients/{id}/vitals` | Історія. Query: `from`, `to`, `limit` (макс. 1000) |
| `GET` | `/api/patients/{id}/vitals/export` | Клінічний експорт історії (CSV або JSON). Query: `format=csv`, `from`, `to`, `limit` |

### Автентифікація та доступ

| Метод | Шлях | Опис |
|-------|------|------|
| `POST` | `/api/auth/login` | Вхід для медичного персоналу (`Doctor`, `Nurse`, `Admin`) |
| `POST` | `/api/auth/device-token` | Генерація токена для IoT-сенсорів (`Device`) |
| `GET` | `/api/auth/me` | Дані та ролі поточного автентифікованого користувача |

### Діагностика та стан системи (Health Checks)

| Метод | Шлях | Опис |
|-------|------|------|
| `GET` | `/healthz` | Загальний статус працездатності сервісу |
| `GET` | `/healthz/ready` | Перевірка готовності (включаючи доступність PostgreSQL) |
| `GET` | `/healthz/live` | Liveness перевірка для контейнерів та оркестраторів |

## SignalR контракт

**URL:** `/hubs/monitoring` (підтримує передачу токена через `?access_token=...`)

**Методи сервер→клієнт:**
| Подія | Параметр | Опис |
|-------|----------|------|
| `VitalSignsReceived` | `VitalSignsResponse` | Нові показники пацієнта |
| `PatientAlert` | `PatientAlertNotification` | Стан вийшов за межі норми |

**Методи клієнт→сервер:**
| Метод | Параметр | Опис |
|-------|----------|------|
| `SubscribeToPatient` | `Guid patientId` | Підписка на конкретного пацієнта |
| `UnsubscribeFromPatient` | `Guid patientId` | Відписка |
| `SubscribeToAll` | — | Підписка на всіх пацієнтів |
| `UnsubscribeFromAll` | — | Відписка від всіх |

**Примітка:** Не підписуйтеся одночасно на конкретного пацієнта й на `All` — повідомлення прийдуть двічі.

**З'єднання з автоматичним реконектом:**
```javascript
const hub = new signalR.HubConnectionBuilder()
    .withUrl('/hubs/monitoring')
    .withAutomaticReconnect([0, 2000, 5000, 10000, 30000])
    .build();
```

## Налаштування порогів

У файлі `appsettings.json` секція `VitalThresholds`:

| Показник | `WarningLow` | `WarningHigh` | `CriticalLow` | `CriticalHigh` |
|----------|-------------|---------------|---------------|----------------|
| `HeartRate` | 50 | 110 | 40 | 130 уд/хв |
| `SystolicBloodPressure` | 90 | 140 | 80 | 180 мм рт.ст. |
| `DiastolicBloodPressure` | 60 | 90 | 40 | 120 мм рт.ст. |
| `OxygenSaturation` | 92 | — | 88 | — % |
| `Temperature` | 36.0 | 38.0 | 35.0 | 39.5 °C |
| `RespiratoryRate` | 12 | 20 | 8 | 30 /хв |

Порожнє (`null`) поле означає, що межа не використовується.

## Зовнішні канали сповіщень (Alert Channels)

Система підтримує паралельну доставку тривог через кілька каналів за допомогою `IAlertDispatcher`:
1. **Webhook** (`AlertChannels:Webhook`): Відправка JSON-payload на довільну зовнішню HTTP/HTTPS кінцеву точку.
2. **Telegram Bot** (`AlertChannels:Telegram`): Відправка форматованих повідомлень з емодзі (🚨 КРИТИЧНИЙ СТАН, ⚠️ ПОПЕРЕДЖЕННЯ) у медичні групи/чати з авто-екрануванням HTML та UTF-8 кирилиці.

## Кешування (MemoryCache та Redis)

Шар швидкого доступу до останніх показників пацієнтів реалізовано через інтерфейс `IVitalSignsCache`:
- **In-Memory Cache** (`MemoryVitalSignsCache`): використовується за замовчуванням при локальному запуску.
- **Distributed Redis Cache** (`DistributedVitalSignsCache`): автоматично активується, якщо в конфігурації вказано рядок `ConnectionStrings:Redis`.

## Тестування

Для запуску повного набору тестів (**64 автоматизовані тести**: 47 модульних + 17 інтеграційних):

```powershell
dotnet test
```

### Модульні тести (`PatientMonitoring.UnitTests` — 50 тестів)
- `VitalSignsAnalyzerTests`: перевірка порогових значень та генерація сповіщень
- `ValidationTests`: перевірка валідації DTO (систолічний > діастолічний, не майбутній час тощо)
- `PatientServiceTests`: CRUD операції з пацієнтами та інвалідація кешу
- `VitalSignsServiceTests`: запис, SignalR-нотифікації, фільтрація історії, пакетний імпорт та виклик Alert Dispatcher
- `VitalSignsCacheTests`: робота шару пам'яті кешування останніх показників (Hit/Miss/Invalidate)
- `DistributedVitalSignsCacheTests`: відмовостійка робота розподіленого кешу (`IDistributedCache`)
- `AlertDispatcherTests`: ізольована відмовостійка доставка сповіщень за кількома каналами
- `TelegramAlertChannelTests`: генерація HTML-сповіщень та HTTP-взаємодія з Telegram Bot API
- `RealtimeRelayTests`: відправка даних у зовнішній веб-додаток та SSE-потік
- `AuthServiceTests`: випуск та валідація JWT токенів для персоналу та сенсорів
- `DatabaseHealthCheckTests`: перевірка доступності БД через Health Checks
- `ControllersTests`: перевірка статусів відповідей контролерів, імпорту та експорту

### Інтеграційні тести (`PatientMonitoring.IntegrationTests` — 19 тестів)
Тести виконуються через `WebApplicationFactory<Program>` з ізольованою базою InMemory:
- `HealthChecksIntegrationTests`: перевірка кінцевих точок `/healthz`, `/healthz/ready`, `/healthz/live`
- `AuthEndpointsIntegrationTests`: повний цикл входу персоналу, випуск токенів сенсорів та валідація `/api/auth/me`
- `PatientsApiIntegrationTests`: наскрізні HTTP-запити на створення, отримання, оновлення та деактивацію пацієнтів
- `VitalSignsApiIntegrationTests`: запис вимірювань, перевірка статусу Normal/Critical, пакетне завантаження, експорт у CSV та універсальний Ingest для парсерів

## Структура проєкту

```
D:\dataS\
├── docker-compose.yml                          # PostgreSQL + API сервіси
├── PatientMonitoring.slnx                      # Solution
├── .gitignore
├── README.md
├── src\PatientMonitoring.Api\                  # Головний бекенд API
│   ├── Dockerfile                              # Мультистейдж Dockerfile
│   ├── PatientMonitoring.Api.csproj
│   ├── Program.cs                              # Конфігурація DI, автентифікації, кешу, SignalR, Relay
│   ├── appsettings.json
│   ├── Migrations/                             # Міграції EF Core PostgreSQL
│   ├── wwwroot\index.html                      # Інтерактивний дашборд реального часу
│   ├── Models\                                 # Patient, VitalSign, VitalStatus
│   ├── Data\AppDbContext.cs                    # EF Core DbContext
│   ├── Contracts\                              # DTO пацієнтів, показників та авторизації
│   ├── Services\                               # Бізнес-логіка, аналізатор порогів, нотифікатор
│   │   ├── Alerting\                           # Webhook та Telegram Bot канали сповіщень
│   │   ├── Auth\                               # JWT сервіс генерації токенів персоналу та пристроїв
│   │   ├── Caching\                            # Memory та Redis кешування останніх показників
│   │   └── Relay\                              # HTTP Data Relay та SSE потік для зовнішніх додатків
│   ├── Hubs\                                   # SignalR Hub
│   └── Controllers\                            # REST контролери (Patients, Vitals, Auth, Ingest, SSE)
├── src\PatientMonitoring.ReceiverService\      # Тестовий веб-застосунок (Receiver App, порт 5050)
├── src\PatientMonitoring.Parser\               # C# Парсер медичного обладнання (Serial, HL7, CSV)
├── tests\PatientMonitoring.UnitTests\          # Модульні тести (xUnit + Moq)
└── tests\PatientMonitoring.IntegrationTests\     # Інтеграційні тести (WebApplicationFactory)
```

## Статус розробки

- [x] Виправлення синтаксичних та runtime помилок .NET 10
- [x] Автоматична міграція та ініціалізація PostgreSQL
- [x] JWT-автентифікація для лікарів, медсестер та медичних пристроїв
- [x] Батчеве надсилання показників (для сенсорів після втрати зв'язку)
- [x] Універсальний Ingest ендпоінт для парсерів (`POST /api/vitals/ingest`)
- [x] Клінічний експорт історії вимірювань у форматі CSV та JSON
- [x] Мультистейдж Dockerfile для API
- [x] Кешування останніх показників (`MemoryVitalSignsCache` + `DistributedVitalSignsCache` Redis)
- [x] Зовнішній диспетчер сповіщень (`IAlertDispatcher`, Webhook + Telegram Bot канали)
- [x] Real-time Data Relay на зовнішній готовий додаток + потік Server-Sent Events (SSE)
- [x] Окремий тестовий веб-сервіс отримувача (`PatientMonitoring.ReceiverService` на порту 5050)
- [x] C# парсер медичних даних з симулятором реальних протоколів (`PatientMonitoring.Parser`)
- [x] Health Checks перевірка працездатності (`/healthz`, `/healthz/ready`, `/healthz/live`)
- [x] Інтерактивний UI реального часу з фільтрами, пресетами, журналом та експортом
- [x] 100% проходження тестів (**69 тестів**: 50 модульних + 19 інтеграційних)

