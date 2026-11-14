using FastEndpoints;
using System.Text.Json.Serialization;
using FastEndpoints.Swagger;
using MedicalMonitorServer;
using MedicalMonitorServer.Contracts.Api;
using MedicalMonitorServer.Core.Monitoring;
using MedicalMonitorServer.DeviceDriver;
using MedicalMonitorServer.Hubs;
using MedicalMonitorServer.Services;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.

builder.Services.AddControllers();
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
builder.Services.SwaggerDocument();

// Monitoring API consumed by the nurse-station UI (docs/API_CONTRACT.md in MedicalMonitor_Desktop).
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowLocalhost", policy => policy
        .SetIsOriginAllowed(IsLocalhost)
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
builder.Services.AddSingleton<ConnectionTracker>();
builder.Services.AddSingleton<IMonitorPipeline, MonitorPipeline>();
builder.Services.AddHostedService<UmecFeedService>();

var app = builder.Build();

// No HTTPS redirection: the station runs over plain HTTP on the ward LAN, and a redirect
// would break the WebSocket upgrade for clients on the http endpoint.
app.UseDefaultFiles();
app.UseStaticFiles();

app.UseCors("AllowLocalhost");

app.UseAuthorization();

app.UseFastEndpoints(config =>
{
    config.Serializer.Options.Converters.Add(new JsonStringEnumConverter());
});

if (app.Environment.IsDevelopment())
{
    app.UseSwaggerGen();
}

app.MapHub<VitalsHub>(HubMethods.Route);
app.MapControllers();

app.Run();

static bool IsLocalhost(string origin)
{
    // A malformed Origin header must be rejected, not thrown on.
    if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri)) return false;

    return uri.Host is "localhost" or "127.0.0.1" or "::1" or "[::1]";
}
