using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace MedicalMonitorServer.Security;

/// <summary>
/// Shared-secret keys for the REST API. Both are required at startup so the server never
/// listens on the ward LAN with open write endpoints.
/// </summary>
public sealed class ApiKeyOptions
{
    public const string Section = "Security";
    public const string HeaderName = "X-Api-Key";

    /// <summary>Key for monitor gateways calling /api/v1/ingestion/*.</summary>
    public string? IngestionKey { get; set; }

    /// <summary>Key for state-changing actions (patients CRUD, acknowledging alerts).</summary>
    public string? OperatorKey { get; set; }

    /// <summary>
    /// Treat requests from this machine (loopback) as the operator without a key, so the
    /// nurse-station web UI (StationUi:Path) can silence alerts and manage patients. Covers
    /// the hub and the operator REST routes, never ingestion. Stations elsewhere on the LAN
    /// still need the operator key. Turn off if a reverse proxy runs on this machine, since
    /// every proxied client would then look local.
    /// </summary>
    public bool TrustLocalStations { get; set; } = true;

    /// <summary>
    /// Whether <paramref name="http"/> comes from a trusted local station: loopback, and not
    /// sent by a web page of another site. Any page open in a browser on this machine also
    /// connects from loopback, so a request whose Origin is not this machine is refused.
    /// Browsers always send Origin on cross-site writes and WebSocket handshakes; a request
    /// without one comes from a tool on this machine, not from a web page.
    /// </summary>
    public bool IsTrustedLocal(HttpContext? http)
    {
        if (!TrustLocalStations || http is null) return false;
        if (http.Connection.RemoteIpAddress is not { } remote || !System.Net.IPAddress.IsLoopback(remote)) return false;

        var origin = http.Request.Headers.Origin.ToString();
        if (string.IsNullOrEmpty(origin)) return true;
        return Uri.TryCreate(origin, UriKind.Absolute, out var uri)
            && uri.Host is "localhost" or "127.0.0.1" or "::1" or "[::1]";
    }

    public static bool Matches(string? provided, string? expected)
    {
        if (string.IsNullOrEmpty(provided) || string.IsNullOrEmpty(expected)) return false;

        // Hash first so the comparison is constant-time regardless of length.
        var a = SHA256.HashData(Encoding.UTF8.GetBytes(provided));
        var b = SHA256.HashData(Encoding.UTF8.GetBytes(expected));
        return CryptographicOperations.FixedTimeEquals(a, b);
    }
}

/// <summary>
/// Ingestion routes need the ingestion key; any other non-read /api request needs the operator key,
/// except from a trusted local station (see <see cref="ApiKeyOptions.IsTrustedLocal"/>).
/// Read-only GETs stay open for the nurse stations, but for the recorded history
/// (/api/v1/history), which is read like an operator route.
/// </summary>
public sealed class ApiKeyMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ApiKeyOptions _options;
    private readonly ILogger<ApiKeyMiddleware> _logger;

    public ApiKeyMiddleware(RequestDelegate next, IOptions<ApiKeyOptions> options, ILogger<ApiKeyMiddleware> logger)
    {
        _next = next;
        _options = options.Value;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var (expected, isOperatorRoute) = RequiredKey(context.Request);

        // The station on this machine manages patients without a key; ingestion never skips it.
        var trustedLocal = isOperatorRoute && _options.IsTrustedLocal(context);

        if (expected is not null && !trustedLocal &&
            !ApiKeyOptions.Matches(context.Request.Headers[ApiKeyOptions.HeaderName].ToString(), expected))
        {
            _logger.LogWarning("Rejected {Method} {Path} from {Remote}: missing or invalid API key.",
                context.Request.Method, context.Request.Path, context.Connection.RemoteIpAddress);
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsJsonAsync(new { error = $"Missing or invalid {ApiKeyOptions.HeaderName} header." });
            return;
        }

        await _next(context);
    }

    /// <returns>The key the request needs (null: none) and whether it is an operator route.</returns>
    private (string? Key, bool IsOperatorRoute) RequiredKey(HttpRequest request)
    {
        var path = request.Path;
        if (path.StartsWithSegments("/api/v1/ingestion", StringComparison.OrdinalIgnoreCase))
            return (_options.IngestionKey, false);

        // The recorded history names every patient who was ever admitted here, with their
        // values: reading it is an operator's business, not the ward network's.
        if (path.StartsWithSegments("/api/v1/history", StringComparison.OrdinalIgnoreCase))
            return (_options.OperatorKey, true);

        if (path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase) &&
            !HttpMethods.IsGet(request.Method) && !HttpMethods.IsHead(request.Method) && !HttpMethods.IsOptions(request.Method))
            return (_options.OperatorKey, true);

        return (null, false);
    }
}
