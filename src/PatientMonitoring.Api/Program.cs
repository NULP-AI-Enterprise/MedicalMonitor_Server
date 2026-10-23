using FastEndpoints;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using PatientMonitoring.Api.Contracts;
using PatientMonitoring.Api.Data;
using PatientMonitoring.Api.Hubs;
using PatientMonitoring.Api.Services;
using PatientMonitoring.Api.Services.Alerting;
using PatientMonitoring.Api.Services.Auth;
using PatientMonitoring.Api.Services.Caching;
using PatientMonitoring.Api.Services.Relay;
using Scalar.AspNetCore;
using System.Text;
using System.Text.Json.Serialization;

var builder = WebApplication.CreateBuilder(args);

// ── Налаштування сервісів ───────────────────────────────────────────────
builder.Services.AddFastEndpoints();
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
});

// .NET 10: вбудований OpenAPI
builder.Services.AddOpenApi();

// Конфігурація порогів
builder.Services.AddOptions<VitalThresholds>()
    .Bind(builder.Configuration.GetRequiredSection(VitalThresholds.SectionName))
    .ValidateOnStart();

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

// Контракт Central Nurse Station (MedicalMonitor_Desktop)
builder.Services.AddSingleton<Monitoring.BL.Services.IAlertStateService, Monitoring.BL.Services.AlertStateService>();
builder.Services.AddSingleton<PatientMonitoring.Api.Services.CentralStation.ICentralStationService, PatientMonitoring.Api.Services.CentralStation.CentralStationService>();
builder.Services.AddHostedService<PatientMonitoring.Api.Services.CentralStation.CentralStationVitalsHostedService>();

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

// SignalR (JSON-протокол)
builder.Services.AddSignalR()
    .AddJsonProtocol(opts =>
    {
        opts.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter());
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
            return uri.Host is "localhost" or "127.0.0.1" or "::1";
        })
        .AllowAnyHeader()
        .AllowAnyMethod()
        .AllowCredentials());
});

var app = builder.Build();

// ── Початкова міграція БД (робиться автоматично) ──────────────────────────
using (var scope = app.Services.CreateAsyncScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    if (db.Database.IsRelational())
    {
        await db.Database.MigrateAsync();
    }
    else
    {
        await db.Database.EnsureCreatedAsync();
    }
}

// ── Middleware ─────────────────────────────────────────────────────────────
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();               // /openapi/v1.json
    app.MapScalarApiReference();    // /scalar/v1
    app.UseSwaggerUI(options =>     // /swagger
    {
        options.SwaggerEndpoint("/openapi/v1.json", "Patient Monitoring API v1");
        options.DocumentTitle = "Patient Monitoring API — Swagger UI";
    });
}

app.UseExceptionHandler("/error");
app.UseStatusCodePagesWithReExecute("/error/{0}");

// Статичні файли Web UI Central Nurse Station (якщо доступні)
var desktopWwwRoot = Path.GetFullPath(Path.Combine(app.Environment.ContentRootPath, "..", "..", "MedicalMonitor_Desktop", "src", "Monitoring.WebService", "wwwroot"));
if (Directory.Exists(desktopWwwRoot))
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

app.UseDefaultFiles();
app.UseStaticFiles();
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
    c.Serializer.Options.Converters.Add(new JsonStringEnumConverter());
});

app.MapHub<PatientMonitoringHub>("/hubs/monitoring");
app.MapHub<PatientMonitoring.Api.Hubs.VitalsHub>(Monitoring.Shared.HubMethods.Route);

// ── Точки перехоплення помилок ────────────────────────────────────────────
app.Map("/error", () => Results.Problem());
app.Map("/error/{statusCode:int}", (int statusCode) => Results.Problem(title: $"Error {statusCode}", statusCode: statusCode));

app.Run();

public partial class Program { }
