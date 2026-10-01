using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PatientMonitoring.Api.Contracts;
using PatientMonitoring.Api.Models;
using PatientMonitoring.Api.Services.Alerting;

namespace PatientMonitoring.UnitTests;

public class TelegramAlertChannelTests
{
    private class TestHttpMessageHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handlerFunc) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => handlerFunc(request);
    }

    [Fact]
    public void IsEnabled_ReturnsExpectedStatus()
    {
        var disabledOpts = Options.Create(new TelegramAlertOptions { Enabled = false, BotToken = "token", ChatId = "123" });
        var channel1 = new TelegramAlertChannel(disabledOpts, new HttpClient(), NullLogger<TelegramAlertChannel>.Instance);
        Assert.False(channel1.IsEnabled);

        var missingToken = Options.Create(new TelegramAlertOptions { Enabled = true, BotToken = "", ChatId = "123" });
        var channel2 = new TelegramAlertChannel(missingToken, new HttpClient(), NullLogger<TelegramAlertChannel>.Instance);
        Assert.False(channel2.IsEnabled);

        var enabledOpts = Options.Create(new TelegramAlertOptions { Enabled = true, BotToken = "token123", ChatId = "456" });
        var channel3 = new TelegramAlertChannel(enabledOpts, new HttpClient(), NullLogger<TelegramAlertChannel>.Instance);
        Assert.True(channel3.IsEnabled);
    }

    [Fact]
    public async Task SendAlertAsync_WhenDisabled_DoesNotMakeHttpRequest()
    {
        var invoked = false;
        var handler = new TestHttpMessageHandler(req =>
        {
            invoked = true;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        });

        var opts = Options.Create(new TelegramAlertOptions { Enabled = false, BotToken = "token", ChatId = "123" });
        var channel = new TelegramAlertChannel(opts, new HttpClient(handler), NullLogger<TelegramAlertChannel>.Instance);

        var alert = new PatientAlertNotification(Guid.NewGuid(), "Іван", VitalStatus.Critical, DateTime.UtcNow, []);
        await channel.SendAlertAsync(alert);

        Assert.False(invoked);
    }

    [Fact]
    public async Task SendAlertAsync_WhenEnabled_SendsTelegramMessageWithCorrectUrlAndContent()
    {
        HttpRequestMessage? capturedRequest = null;
        string? capturedBody = null;

        var handler = new TestHttpMessageHandler(async req =>
        {
            capturedRequest = req;
            capturedBody = await req.Content!.ReadAsStringAsync();
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        var opts = Options.Create(new TelegramAlertOptions { Enabled = true, BotToken = "bot12345", ChatId = "-999" });
        var channel = new TelegramAlertChannel(opts, new HttpClient(handler), NullLogger<TelegramAlertChannel>.Instance);

        var alert = new PatientAlertNotification(
            Guid.NewGuid(),
            "Тарас Шевченко",
            VitalStatus.Critical,
            DateTime.UtcNow,
            [new VitalAlert("HeartRate", 160, VitalStatus.Critical, "Тахікардія")]);

        await channel.SendAlertAsync(alert);

        Assert.NotNull(capturedRequest);
        Assert.Equal("https://api.telegram.org/botbot12345/sendMessage", capturedRequest.RequestUri?.ToString());
        Assert.NotNull(capturedBody);
        Assert.Contains("-999", capturedBody);
        Assert.Contains("Тарас Шевченко", capturedBody);
        Assert.Contains("HeartRate", capturedBody);
    }

    [Fact]
    public async Task SendAlertAsync_WhenHttpErrorOccurs_DoesNotThrow()
    {
        var handler = new TestHttpMessageHandler(_ =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)));

        var opts = Options.Create(new TelegramAlertOptions { Enabled = true, BotToken = "bot12345", ChatId = "-999" });
        var channel = new TelegramAlertChannel(opts, new HttpClient(handler), NullLogger<TelegramAlertChannel>.Instance);

        var alert = new PatientAlertNotification(Guid.NewGuid(), "Олена", VitalStatus.Warning, DateTime.UtcNow, []);

        // Should not throw exception
        await channel.SendAlertAsync(alert);
    }
}
