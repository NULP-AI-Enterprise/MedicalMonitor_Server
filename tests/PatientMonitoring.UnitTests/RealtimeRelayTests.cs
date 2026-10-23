using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PatientMonitoring.Api.Contracts;
using PatientMonitoring.Api.Models;
using PatientMonitoring.Api.Services.Relay;

namespace PatientMonitoring.UnitTests;

public class RealtimeRelayTests
{
    private class TestHttpMessageHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handlerFunc) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => handlerFunc(request);
    }

    [Fact]
    public async Task HttpDataRelay_WhenDisabled_DoesNotSendRequest()
    {
        var called = false;
        var handler = new TestHttpMessageHandler(req =>
        {
            called = true;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        });

        var options = Options.Create(new DataRelayOptions { Enabled = false, TargetUrl = "http://example.com/relay" });
        var relay = new HttpDataRelay(options, new HttpClient(handler), NullLogger<HttpDataRelay>.Instance);

        var vitals = new VitalSignsResponse(1, Guid.NewGuid(), DateTime.UtcNow, DateTime.UtcNow, null, 80, null, null, null, null, null, VitalStatus.Normal, []);
        await relay.RelayVitalSignsAsync(vitals);

        Assert.False(called);
    }

    [Fact]
    public async Task HttpDataRelay_WhenEnabled_SendsPostWithHeaderAndPayload()
    {
        HttpRequestMessage? capturedRequest = null;
        string? capturedBody = null;

        var handler = new TestHttpMessageHandler(async req =>
        {
            capturedRequest = req;
            capturedBody = await req.Content!.ReadAsStringAsync();
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        var options = Options.Create(new DataRelayOptions
        {
            Enabled = true,
            TargetUrl = "http://external-app.internal/api/vitals-inbox",
            ApiKey = "secret-token-xyz",
            HeaderName = "X-Service-Token"
        });

        var relay = new HttpDataRelay(options, new HttpClient(handler), NullLogger<HttpDataRelay>.Instance);
        var patientId = Guid.NewGuid();
        var vitals = new VitalSignsResponse(1, patientId, DateTime.UtcNow, DateTime.UtcNow, "BED-01", 78, 120, 80, 99.0, 36.6, 16, VitalStatus.Normal, [], "Олена");

        await relay.RelayVitalSignsAsync(vitals);

        Assert.NotNull(capturedRequest);
        Assert.Equal(HttpMethod.Post, capturedRequest.Method);
        Assert.Equal("http://external-app.internal/api/vitals-inbox", capturedRequest.RequestUri?.ToString());
        Assert.True(capturedRequest.Headers.Contains("X-Service-Token"));
        Assert.Equal("secret-token-xyz", capturedRequest.Headers.GetValues("X-Service-Token").First());

        Assert.NotNull(capturedBody);
        Assert.Contains("vital_signs_recorded", capturedBody);
        Assert.Contains(patientId.ToString(), capturedBody);
        Assert.Contains("Олена", capturedBody);
    }

    [Fact]
    public async Task SseStreamService_Broadcast_DeliversToSubscribers()
    {
        var sse = new SseStreamService();
        var patientId = Guid.NewGuid();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        var vitals = new VitalSignsResponse(1, patientId, DateTime.UtcNow, DateTime.UtcNow, null, 75, null, null, null, null, null, VitalStatus.Normal, []);

        var enumerator = sse.SubscribeAsync(patientId, cts.Token).GetAsyncEnumerator(cts.Token);

        // MoveNextAsync in background task
        var readTask = enumerator.MoveNextAsync().AsTask();

        // Broadcast
        sse.Broadcast(vitals);

        var hasItem = await readTask;
        Assert.True(hasItem);
        Assert.Equal(patientId, enumerator.Current.PatientId);
        Assert.Equal(75, enumerator.Current.HeartRate);
    }
}
