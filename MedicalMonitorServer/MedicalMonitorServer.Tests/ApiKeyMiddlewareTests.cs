using System.Net;
using MedicalMonitorServer.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace MedicalMonitorServer.Tests;

/// <summary>
/// Who may write without a key: the station on this machine may manage patients and silence
/// alerts, but never ingest, and a web page of another site open on this machine may do neither.
/// </summary>
public class ApiKeyMiddlewareTests
{
    private const string OperatorKey = "op-key";
    private const string IngestionKey = "in-key";

    private static ApiKeyOptions Options(bool trustLocal = true) => new()
    {
        OperatorKey = OperatorKey,
        IngestionKey = IngestionKey,
        TrustLocalStations = trustLocal,
    };

    private static HttpContext Request(string method, string path, string remoteIp, string? origin = null, string? key = null)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = method;
        context.Request.Path = path;
        context.Connection.RemoteIpAddress = IPAddress.Parse(remoteIp);
        if (origin is not null) context.Request.Headers.Origin = origin;
        if (key is not null) context.Request.Headers[ApiKeyOptions.HeaderName] = key;
        context.Response.Body = new MemoryStream();
        return context;
    }

    /// <returns>Whether the request reached the endpoint.</returns>
    private static async Task<bool> Passes(HttpContext context, ApiKeyOptions? options = null)
    {
        var reached = false;
        var middleware = new ApiKeyMiddleware(
            _ => { reached = true; return Task.CompletedTask; },
            Microsoft.Extensions.Options.Options.Create(options ?? Options()),
            NullLogger<ApiKeyMiddleware>.Instance);

        await middleware.InvokeAsync(context);
        return reached;
    }

    [Theory]
    [InlineData(null)]
    [InlineData("http://localhost:5230")]
    [InlineData("http://127.0.0.1:5230")]
    public async Task Local_station_manages_patients_without_a_key(string? origin)
    {
        Assert.True(await Passes(Request("POST", "/api/v1/patients", "127.0.0.1", origin)));
        Assert.True(await Passes(Request("DELETE", "/api/v1/patients/P-1", "::1", origin)));
        Assert.True(await Passes(Request("POST", "/api/v1/patients/P-1/transfer", "127.0.0.1", origin)));
    }

    [Fact]
    public async Task Page_of_another_site_on_this_machine_is_refused()
    {
        var context = Request("POST", "/api/v1/patients", "127.0.0.1", origin: "https://evil.example");

        Assert.False(await Passes(context));
        Assert.Equal(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
    }

    [Fact]
    public async Task Malformed_origin_is_refused()
    {
        Assert.False(await Passes(Request("POST", "/api/v1/patients", "127.0.0.1", origin: "not a url")));
    }

    [Fact]
    public async Task Ingestion_always_needs_its_key_even_locally()
    {
        Assert.False(await Passes(Request("POST", "/api/v1/ingestion/telemetry", "127.0.0.1")));
        Assert.True(await Passes(Request("POST", "/api/v1/ingestion/telemetry", "127.0.0.1", key: IngestionKey)));
    }

    [Fact]
    public async Task Remote_station_needs_the_operator_key()
    {
        Assert.False(await Passes(Request("POST", "/api/v1/patients", "192.168.2.50", origin: "http://localhost:5230")));
        Assert.True(await Passes(Request("POST", "/api/v1/patients", "192.168.2.50", key: OperatorKey)));
    }

    [Fact]
    public async Task Local_trust_can_be_switched_off()
    {
        Assert.False(await Passes(Request("POST", "/api/v1/patients", "127.0.0.1"), Options(trustLocal: false)));
    }

    [Fact]
    public async Task Reads_stay_open()
    {
        Assert.True(await Passes(Request("GET", "/api/v1/monitors", "192.168.2.50")));
    }

    [Theory]
    [InlineData("/api/v1/history/patients")]
    [InlineData("/api/v1/history/patients/0b9c5e2a-6d0f-4c56-9f0e-3a2f8f1d7c11")]
    [InlineData("/api/v1/history/patients/0b9c5e2a-6d0f-4c56-9f0e-3a2f8f1d7c11/vitals")]
    public async Task History_is_read_with_the_operator_key_or_from_this_machine(string path)
    {
        // It names every patient who was ever admitted: not for anybody on the ward network.
        var refused = Request("GET", path, "192.168.2.50");
        Assert.False(await Passes(refused));
        Assert.Equal(StatusCodes.Status401Unauthorized, refused.Response.StatusCode);

        Assert.True(await Passes(Request("GET", path, "192.168.2.50", key: OperatorKey)));
        Assert.True(await Passes(Request("GET", path, "127.0.0.1", origin: "http://localhost:5230")));
        Assert.False(await Passes(Request("GET", path, "192.168.2.50", key: IngestionKey)));
        Assert.False(await Passes(Request("GET", path, "127.0.0.1", origin: "https://evil.example")));
    }

    [Fact]
    public void Hub_trust_follows_the_same_rule()
    {
        var options = Options();

        Assert.True(options.IsTrustedLocal(Request("GET", "/hubs/vitals", "127.0.0.1", "http://localhost:5230")));
        Assert.False(options.IsTrustedLocal(Request("GET", "/hubs/vitals", "127.0.0.1", "https://evil.example")));
        Assert.False(options.IsTrustedLocal(Request("GET", "/hubs/vitals", "10.0.0.7")));
        Assert.False(options.IsTrustedLocal(null));
    }
}
