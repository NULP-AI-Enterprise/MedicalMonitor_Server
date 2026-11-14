using FastEndpoints;
using System.Text.Json.Serialization;
using FastEndpoints.Swagger;
using MedicalMonitorServer;
using MedicalMonitorServer.Contracts.Api;
using MedicalMonitorServer.Core.Monitoring;
using MedicalMonitorServer.DeviceDriver;
using MedicalMonitorServer.Hubs;
using MedicalMonitorServer.Security;
using MedicalMonitorServer.Services;

var builder = WebApplication.CreateBuilder(args);

// Listening address comes from "Urls" in appsettings.json (localhost only by default). To let stations
// and gateways on the ward LAN connect, set it to the LAN address, e.g. http://192.168.1.10:5230,
// or override with ASPNETCORE_URLS / --urls.

var apiKeys = builder.Configuration.GetSection(ApiKeyOptions.Section).Get<ApiKeyOptions>() ?? new ApiKeyOptions();
if (string.IsNullOrWhiteSpace(apiKeys.IngestionKey) || string.IsNullOrWhiteSpace(apiKeys.OperatorKey))
{
    // Fail closed: the server is reachable from the LAN, so it must not start with open write endpoints.
    throw new InvalidOperationException(
        "Security:IngestionKey and Security:OperatorKey must be set (appsettings, or env vars " +
        "Security__IngestionKey / Security__OperatorKey).");
}

builder.Services.Configure<ApiKeyOptions>(builder.Configuration.GetSection(ApiKeyOptions.Section));

var swaggerEnabled = builder.Environment.IsDevelopment() || builder.Configuration.GetValue<bool>("Swagger:Enabled");
var extraOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? Array.Empty<string>();

builder.Services.AddSingleton(serviceProvider =>
{
    var options = new WatchDogOptions();
    serviceProvider
        .GetRequiredService<IConfiguration>()
        .GetSection("WatchDog")
        .Bind(options);
    return options;
});
builder.Services.AddSingleton<IWatchDogService, WatchDogService>();
builder.Services.AddSingleton<DeviceService>();
builder.Services.AddHostedService<WatchDogHostedService>();
builder.Services.AddFastEndpoints();
if (swaggerEnabled)
{
    builder.Services.SwaggerDocument();
}

// Monitoring API consumed by the nurse-station UI (docs/API_CONTRACT.md in MedicalMonitor_Desktop).
builder.Services.AddCors(options =>
{
    // Browser UIs served from this machine are always allowed; LAN UIs are listed in Cors:AllowedOrigins.
    options.AddPolicy("AllowStations", policy => policy
        .SetIsOriginAllowed(origin => IsLocalhost(origin) || extraOrigins.Contains(origin, StringComparer.OrdinalIgnoreCase))
        .AllowAnyHeader()
        .AllowAnyMethod()
        .AllowCredentials());
});

builder.Services.AddSignalR()
    .AddJsonProtocol(options =>
    {
        // Enums travel by name ("ECG_II", "Critical"), never by ordinal.
        options.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter());
    });

builder.Services.AddSingleton<IPatientRegistry, PatientRegistry>();
builder.Services.AddSingleton<IAlertStateService, AlertStateService>();
builder.Services.AddSingleton<IMonitorBindings, MonitorBindings>();
builder.Services.AddSingleton<MonitorRouter>();
builder.Services.AddSingleton<ConnectionTracker>();
builder.Services.AddSingleton<FeedStatus>();
builder.Services.AddSingleton<IMonitorPipeline, MonitorPipeline>();
builder.Services.AddHostedService<UmecFeedService>();

var app = builder.Build();

// No HTTPS redirection: the station runs over plain HTTP on the ward LAN, and a redirect
// would break the WebSocket upgrade for clients on the http endpoint.
app.UseDefaultFiles();
app.UseStaticFiles();

app.UseCors("AllowStations");

app.UseMiddleware<ApiKeyMiddleware>();

app.UseAuthorization();

app.UseFastEndpoints(config =>
{
    config.Serializer.Options.Converters.Add(new JsonStringEnumConverter());
});

if (swaggerEnabled)
{
    app.UseSwaggerGen();
}

app.MapHub<VitalsHub>(HubMethods.Route);

app.Run();

static bool IsLocalhost(string origin)
{
    // A malformed Origin header must be rejected, not thrown on.
    if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri)) return false;

    return uri.Host is "localhost" or "127.0.0.1" or "::1" or "[::1]";
}
