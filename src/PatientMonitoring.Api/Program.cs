using FastEndpoints;
using FastEndpoints.Swagger;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using PatientMonitoring.Api.Contracts;
using PatientMonitoring.Api.Data;
using PatientMonitoring.Api.Hubs;
using PatientMonitoring.Api.Serialization;
using PatientMonitoring.Api.Services;
using PatientMonitoring.Api.Services.Alerting;
using PatientMonitoring.Api.Services.Auth;
using PatientMonitoring.Api.Services.Caching;
using PatientMonitoring.Api.Services.Relay;
using Scalar.AspNetCore;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

// Clinical numbers are written with the invariant culture everywhere ("35.5 °C", never "35,5 °C").
System.Globalization.CultureInfo.DefaultThreadCurrentCulture = System.Globalization.CultureInfo.InvariantCulture;
System.Globalization.CultureInfo.DefaultThreadCurrentUICulture = System.Globalization.CultureInfo.InvariantCulture;

var builder = WebApplication.CreateBuilder(args);

// API Contract §1: the API always listens on port 5000, whatever ASPNETCORE_URLS says. Port 5225 stays for older clients.
builder.WebHost.UseUrls("http://0.0.0.0:5000", "http://0.0.0.0:5225");

// ── Налаштування сервісів ───────────────────────────────────────────────
builder.Services.AddFastEndpoints();
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
    options.SerializerOptions.Converters.Add(new UtcIsoDateTimeOffsetConverter());
});

// Swagger / OpenAPI документація для FastEndpoints
builder.Services.SwaggerDocument(o =>
{
    o.DocumentSettings = s =>
    {
        s.DocumentName = "v1";
        s.Title = "Patient Monitoring API";
        s.Version = "v1";
        s.Description = "Real-time Patient Monitoring API & Central Nurse Station Backend (Mindray uMEC compatible)";
    };
});

// .NET 10: вбудований OpenAPI
builder.Services.AddOpenApi();

// Пороги тривог — фіксовані клінічні значення контракту (Monitoring.Shared.VitalThresholds)
builder.Services.AddOptions<VitalThresholds>();

// База даних
builder.Services.AddDbContext<AppDbContext>(options =>
{
    var conn = builder.Configuration.GetConnectionString("Default");
    if (string.Equals(conn, "InMemory", StringComparison.OrdinalIgnoreCase) ||
        conn?.StartsWith("InMemory:", StringComparison.OrdinalIgnoreCase) == true)
    {
        var dbName = (conn is not null && conn.Contains(':')) ? conn.Split(':')[1] : "PatientMonitoring_InMemory";
        options.UseInMemoryDatabase(dbName);
    }
    else
    {
        options.UseNpgsql(
            conn!,
            npgsql => npgsql.MigrationsAssembly("PatientMonitoring.Api"));
    }
});

// Кешування останніх показників (Redis або In-Memory)
var redisConnectionString = builder.Configuration.GetConnectionString("Redis");
if (!string.IsNullOrWhiteSpace(redisConnectionString))
{
    builder.Services.AddStackExchangeRedisCache(options =>
    {
        options.Configuration = redisConnectionString;
        options.InstanceName = "PatientMonitoring:";
    });
    builder.Services.AddSingleton<IVitalSignsCache, DistributedVitalSignsCache>();
}
else
{
    builder.Services.AddMemoryCache();
    builder.Services.AddSingleton<IVitalSignsCache, MemoryVitalSignsCache>();
}

// Зовнішні канали сповіщень (Alert Channels)
builder.Services.Configure<WebhookAlertOptions>(builder.Configuration.GetSection(WebhookAlertOptions.SectionName));
builder.Services.AddHttpClient<WebhookAlertChannel>();
builder.Services.AddTransient<IAlertChannel>(sp => sp.GetRequiredService<WebhookAlertChannel>());

builder.Services.Configure<TelegramAlertOptions>(builder.Configuration.GetSection(TelegramAlertOptions.SectionName));
builder.Services.AddHttpClient<TelegramAlertChannel>();
builder.Services.AddTransient<IAlertChannel>(sp => sp.GetRequiredService<TelegramAlertChannel>());

builder.Services.AddScoped<IAlertDispatcher, AlertDispatcher>();

// Передача даних у реальному часі на зовнішній веб-додаток та SSE потік
builder.Services.Configure<DataRelayOptions>(builder.Configuration.GetSection(DataRelayOptions.SectionName));
builder.Services.AddHttpClient<IRealtimeDataRelay, HttpDataRelay>();
builder.Services.AddSingleton<ISseStreamService, SseStreamService>();

// Сервіси бізнес-логіки
builder.Services.AddSingleton<IPatientNotifier, SignalRPatientNotifier>();
builder.Services.AddScoped<IVitalSignsAnalyzer, VitalSignsAnalyzer>();
builder.Services.AddScoped<IPatientService, PatientService>();
builder.Services.AddScoped<IVitalSignsService, VitalSignsService>();
builder.Services.AddSingleton<MedicalMonitorServer.DeviceDriver.Umec.IParserService, MedicalMonitorServer.DeviceDriver.Umec.ParserService>();
builder.Services.AddScoped<PatientMonitoring.Api.Services.Parser.IUmecParserBridge, PatientMonitoring.Api.Services.Parser.UmecParserBridge>();

// Контракт Central Nurse Station (MedicalMonitor_Desktop)
builder.Services.AddSingleton<Monitoring.BL.Services.IAlertStateService, Monitoring.BL.Services.AlertStateService>();
builder.Services.AddSingleton<PatientMonitoring.Api.Services.CentralStation.ICentralStationService, PatientMonitoring.Api.Services.CentralStation.CentralStationService>();
builder.Services.AddHostedService<PatientMonitoring.Api.Services.CentralStation.CentralStationVitalsHostedService>();
builder.Services.Configure<PatientMonitoring.Api.Services.CentralStation.CentralStationOptions>(
    builder.Configuration.GetSection(PatientMonitoring.Api.Services.CentralStation.CentralStationOptions.SectionName));

// База даних створюється і мігрується у фоні, щоб недоступна БД не зупиняла API.
builder.Services.AddHostedService<DatabaseMigrationService>();

// JWT Автентифікація
builder.Services.Configure<JwtOptions>(builder.Configuration.GetSection(JwtOptions.SectionName));
builder.Services.AddScoped<IAuthService, AuthService>();

var jwtOptions = builder.Configuration.GetSection(JwtOptions.SectionName).Get<JwtOptions>() ?? new JwtOptions();
builder.Services.AddAuthentication(options =>
{
    options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
    options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
})
.AddJwtBearer(options =>
{
    options.TokenValidationParameters = new TokenValidationParameters
    {
        ValidateIssuer = true,
        ValidateAudience = true,
        ValidateLifetime = true,
        ValidateIssuerSigningKey = true,
        ValidIssuer = jwtOptions.Issuer,
        ValidAudience = jwtOptions.Audience,
        IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtOptions.SecretKey))
    };

    // SignalR передає токен через query string ?access_token=...
    options.Events = new JwtBearerEvents
    {
        OnMessageReceived = context =>
        {
            var accessToken = context.Request.Query["access_token"];
            var path = context.HttpContext.Request.Path;
            if (!string.IsNullOrEmpty(accessToken) && path.StartsWithSegments("/hubs/monitoring"))
            {
                context.Token = accessToken;
            }
            return Task.CompletedTask;
        }
    };
});
builder.Services.AddAuthorization();

// Health Checks (перевірка працездатності БД та сервісу)
builder.Services.AddHealthChecks()
    .AddCheck<DatabaseHealthCheck>("database", tags: ["ready"]);

// SignalR (JSON-протокол з рядковими енумами та camelCase)
builder.Services.AddSignalR()
    .AddJsonProtocol(opts =>
    {
        opts.PayloadSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
        opts.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter());
        opts.PayloadSerializerOptions.Converters.Add(new UtcIsoDateTimeOffsetConverter());
    });

// Перехресні походження (CORS): дозволити фронтенд підключитись до SignalR
var allowedOrigins = builder.Configuration.GetSection("AllowedOrigins").Get<List<string>>() ?? [];
builder.Services.AddCors(options =>
{
    options.AddPolicy("Spa", policy =>
        policy.WithOrigins([.. allowedOrigins])
              .AllowAnyHeader()
              .AllowAnyMethod()
              .AllowCredentials());

    options.AddPolicy("AllowLocalhost", policy =>
        policy.SetIsOriginAllowed(origin =>
        {
            if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri)) return false;
            // Контракт §1: лише http://localhost:*.
            return uri.Scheme == Uri.UriSchemeHttp && uri.Host == "localhost";
        })
        .AllowAnyHeader()
        .AllowAnyMethod()
        .AllowCredentials());
});

var app = builder.Build();

// Міграція БД виконується у фоні (DatabaseMigrationService), тож API слухає порт 5000 навіть без БД.

// ── Middleware ─────────────────────────────────────────────────────────────
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();               // /openapi/v1.json
    app.MapScalarApiReference();    // /scalar/v1
}

app.UseExceptionHandler("/error");
app.UseStatusCodePagesWithReExecute("/error/{0}");

// Статичні файли Web UI Central Nurse Station на кореневому маршруті /
var candidatePaths = new[]
{
    Path.GetFullPath(Path.Combine(app.Environment.ContentRootPath, "..", "..", "MedicalMonitor_Desktop", "src", "Monitoring.WebService", "wwwroot")),
    Path.GetFullPath(Path.Combine(app.Environment.ContentRootPath, "..", "MedicalMonitor_Desktop", "src", "Monitoring.WebService", "wwwroot")),
    Path.GetFullPath(Path.Combine(app.Environment.ContentRootPath, "MedicalMonitor_Desktop", "src", "Monitoring.WebService", "wwwroot")),
    Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "wwwroot")),
    Path.GetFullPath(Path.Combine(app.Environment.ContentRootPath, "wwwroot"))
};

var desktopWwwRoot = candidatePaths.FirstOrDefault(Directory.Exists);
if (desktopWwwRoot is not null)
{
    app.UseDefaultFiles(new DefaultFilesOptions
    {
        FileProvider = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(desktopWwwRoot)
    });
    app.UseStaticFiles(new StaticFileOptions
    {
        FileProvider = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(desktopWwwRoot)
    });
}
else
{
    app.UseDefaultFiles();
    app.UseStaticFiles();
}

app.UseCors("AllowLocalhost");
app.UseRouting();

app.UseAuthentication();
app.UseAuthorization();

// Health Check Endpoints
app.MapHealthChecks("/healthz");
app.MapHealthChecks("/healthz/ready", new HealthCheckOptions { Predicate = check => check.Tags.Contains("ready") });
app.MapHealthChecks("/healthz/live", new HealthCheckOptions { Predicate = _ => false });

app.UseFastEndpoints(c =>
{
    c.Serializer.Options.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    c.Serializer.Options.Converters.Add(new JsonStringEnumConverter());
    c.Serializer.Options.Converters.Add(new UtcIsoDateTimeOffsetConverter());
});

// Swagger Documentation & UI
app.UseSwaggerGen(); // /swagger/v1/swagger.json
app.UseSwaggerUi();  // /swagger

app.MapHub<PatientMonitoringHub>("/hubs/monitoring");
app.MapHub<PatientMonitoring.Api.Hubs.VitalsHub>(Monitoring.Shared.HubMethods.Route);

// ── Точки перехоплення помилок ────────────────────────────────────────────
app.Map("/error", () => Results.Problem());
app.Map("/error/{statusCode:int}", (int statusCode) => Results.Problem(title: $"Error {statusCode}", statusCode: statusCode));

app.Run();

public partial class Program { }
