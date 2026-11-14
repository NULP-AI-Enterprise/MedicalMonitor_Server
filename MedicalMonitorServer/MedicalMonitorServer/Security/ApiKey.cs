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
/// Ingestion routes need the ingestion key; any other non-read /api request needs the operator key.
/// Read-only GETs stay open for the nurse stations.
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
        var expected = RequiredKey(context.Request);
        if (expected is not null &&
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

    private string? RequiredKey(HttpRequest request)
    {
        var path = request.Path;
        if (path.StartsWithSegments("/api/v1/ingestion", StringComparison.OrdinalIgnoreCase))
            return _options.IngestionKey;

        if (path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase) &&
            !HttpMethods.IsGet(request.Method) && !HttpMethods.IsHead(request.Method) && !HttpMethods.IsOptions(request.Method))
            return _options.OperatorKey;

        return null;
    }
}
