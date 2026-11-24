using FastEndpoints;
using System.Globalization;
using System.Text.Json.Serialization;
using NReco.Logging.File;
using FastEndpoints.Swagger;
using MedicalMonitorServer;
using MedicalMonitorServer.Contracts.Api;
using MedicalMonitorServer.Core.Monitoring;
using MedicalMonitorServer.DeviceDriver;
using MedicalMonitorServer.DeviceDriver.NihonKohden;
using MedicalMonitorServer.DeviceDriver.Umec;
using MedicalMonitorServer.Hubs;
using MedicalMonitorServer.Security;
using MedicalMonitorServer.Services;
using Microsoft.Extensions.Hosting.WindowsServices;

// Started by the service control manager the working directory is System32, so a service
// takes its content root (appsettings, logs, patient records, the station UI) from where the
// executable is. Run from a console, the working directory is the content root as before.
var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = WindowsServiceHelpers.IsWindowsService() ? AppContext.BaseDirectory : null,
});

// Does nothing unless the process was started as a Windows service (deploy/Install-Server.ps1).
builder.Host.UseWindowsService(options => options.ServiceName = "MedicalMonitorServer");

// Settings of this one machine - where its copy of the station UI is, say - go in
// appsettings.Local.json next to appsettings.json. The file is not in the repository. It is
// read after the appsettings files and before the environment, so a variable still overrides it.
var localSettings = new Microsoft.Extensions.Configuration.Json.JsonConfigurationSource
{
    Path = "appsettings.Local.json",
    Optional = true,
    ReloadOnChange = false,
};
// The last environment source is the one that holds the application's settings; the ones
// before it carry the host's own (ASPNETCORE_...) and come ahead of the appsettings files.
var environmentAt = builder.Configuration.Sources
    .ToList()
    .FindLastIndex(source => source is Microsoft.Extensions.Configuration.EnvironmentVariables.EnvironmentVariablesConfigurationSource);
builder.Configuration.Sources.Insert(environmentAt < 0 ? builder.Configuration.Sources.Count : environmentAt, localSettings);

// Daily log file next to the console log (Logging:File in appsettings). Alert transitions are logged
// too, so the file is also the alarm history. Relative paths are under the content root.
var fileLog = builder.Configuration.GetSection("Logging:File");
if (fileLog.GetValue<string>("Path") is { Length: > 0 } logPath)
{
    builder.Logging.AddFile(Path.Combine(builder.Environment.ContentRootPath, logPath), options =>
    {
        fileLog.Bind(options); // Append, MinLevel, FileSizeLimitBytes, MaxRollingFiles
        // "{0:yyyy-MM-dd}" in Path rolls the file over at midnight.
        options.FormatLogFileName = name => string.Format(CultureInfo.InvariantCulture, name, DateTime.Now);
    });
}

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

// Nihon Kohden monitors in HL7 mode are listed by address ("NihonKohden:Monitors"); the watchdog
// must not probe them. An empty list leaves the driver off.
builder.Services.AddSingleton(serviceProvider =>
{
    var options = new NihonKohdenOptions();
    serviceProvider
        .GetRequiredService<IConfiguration>()
        .GetSection("NihonKohden")
        .Bind(options);
    return options;
});

// Link timings of the uMec driver. TimeSpan values bind from "hh:mm:ss" strings,
// so the section uses that form. A bare number does NOT throw — it binds as a
// number of DAYS ("ReadTimeout": 10 means ten days) — which is why
// UmecLinkOptions.Validate rejects anything outside a sane range.
builder.Services.AddSingleton(serviceProvider =>
{
    var options = new UmecLinkOptions();
    serviceProvider
        .GetRequiredService<IConfiguration>()
        .GetSection("Umec")
        .Bind(options);
    return options;
});
builder.Services.AddSingleton<DeviceService>();
builder.Services.AddSingleton<IMonitorAdmissions>(serviceProvider => serviceProvider.GetRequiredService<DeviceService>());
builder.Services.AddHostedService<WatchDogHostedService>();
builder.Services.AddFastEndpoints();
if (swaggerEnabled)
{
    builder.Services.SwaggerDocument(o =>
    {
        // Keys are checked by ApiKeyMiddleware, not ASP.NET auth, so declare the header
        // document-wide; Swagger UI's Authorize button then sends it on every request.
        o.EnableJWTBearerAuth = false;
        o.DocumentSettings = s => s.AddSecurity(ApiKeyOptions.HeaderName, Enumerable.Empty<string>(), new NSwag.OpenApiSecurityScheme
        {
            Type = NSwag.OpenApiSecuritySchemeType.ApiKey,
            Name = ApiKeyOptions.HeaderName,
            In = NSwag.OpenApiSecurityApiKeyLocation.Header,
            Description = "Ingestion key for /api/v1/ingestion/*, operator key for other writes. GETs need none."
        });
    });
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

// What stations record about a patient (bed, diagnosis, a name the monitor does not give) is
// kept in "Roster:Path", relative to the content root, so that a restart does not lose it.
// An empty path keeps nothing.
builder.Services.AddSingleton<IPatientRecordStore>(serviceProvider =>
{
    var path = serviceProvider.GetRequiredService<IConfiguration>()["Roster:Path"];
    if (string.IsNullOrWhiteSpace(path)) return new NoPatientRecordStore();

    return new JsonFilePatientRecordStore(
        Path.Combine(serviceProvider.GetRequiredService<IHostEnvironment>().ContentRootPath, path),
        serviceProvider.GetRequiredService<ILogger<JsonFilePatientRecordStore>>());
});
builder.Services.AddSingleton<IAlertStateService, AlertStateService>();
builder.Services.AddSingleton<IMonitorBindings, MonitorBindings>();
builder.Services.AddSingleton<MonitorRouter>();
builder.Services.AddSingleton<ConnectionTracker>();
builder.Services.AddSingleton<FeedStatus>();
builder.Services.AddSingleton<IMonitorPipeline, MonitorPipeline>();
builder.Services.AddHostedService<UmecFeedService>();
builder.Services.AddHostedService<MonitorSilenceService>();

var app = builder.Build();

// No HTTPS redirection: the station runs over plain HTTP on the ward LAN, and a redirect
// would break the WebSocket upgrade for clients on the http endpoint.

// The nurse-station web UI lives in the MedicalMonitor_Desktop repo
// (src/Monitoring.WebService/wwwroot) and is served from there, so the two repos never
// hold diverging copies. StationUi:Path points at that folder; relative paths are under
// the content root. The default assumes both repos are cloned side by side.
var stationUiPath = builder.Configuration["StationUi:Path"];
if (!string.IsNullOrWhiteSpace(stationUiPath))
{
    var root = Path.GetFullPath(Path.Combine(app.Environment.ContentRootPath, stationUiPath));
    if (Directory.Exists(root))
    {
        var files = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(root);
        app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = files });
        app.UseStaticFiles(new StaticFileOptions
        {
            FileProvider = files,
            // Revalidate on every load (cheap: 304 by ETag). Without it a browser may keep a
            // stale index.html next to a new app.js after the UI is updated, and break.
            OnPrepareResponse = ctx => ctx.Context.Response.Headers.CacheControl = "no-cache",
        });
        app.Logger.LogInformation("Serving the station UI from {Path}", root);
    }
    else
    {
        app.Logger.LogWarning(
            "Station UI folder {Path} not found; the API runs without the web UI. " +
            "Clone MedicalMonitor_Desktop next to this repo or set StationUi:Path (env StationUi__Path).", root);
    }
}

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
