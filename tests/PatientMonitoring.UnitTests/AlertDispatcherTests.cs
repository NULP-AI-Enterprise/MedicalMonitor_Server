using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using PatientMonitoring.Api.Contracts;
using PatientMonitoring.Api.Models;
using PatientMonitoring.Api.Services.Alerting;

namespace PatientMonitoring.UnitTests;

public class AlertDispatcherTests
{
    [Fact]
    public async Task DispatchAsync_WhenNoActiveChannels_DoesNotThrow()
    {
        // Arrange
        var dispatcher = new AlertDispatcher([], NullLogger<AlertDispatcher>.Instance);
        var alert = new PatientAlertNotification(Guid.NewGuid(), "Пацієнт", VitalStatus.Critical, DateTime.UtcNow, []);

        // Act & Assert
        await dispatcher.DispatchAsync(alert);
    }

    [Fact]
    public async Task DispatchAsync_InvokesOnlyEnabledChannels()
    {
        // Arrange
        var enabledMock = new Mock<IAlertChannel>();
        enabledMock.SetupGet(c => c.Name).Returns("EnabledChannel");
        enabledMock.SetupGet(c => c.IsEnabled).Returns(true);

        var disabledMock = new Mock<IAlertChannel>();
        disabledMock.SetupGet(c => c.Name).Returns("DisabledChannel");
        disabledMock.SetupGet(c => c.IsEnabled).Returns(false);

        var dispatcher = new AlertDispatcher([enabledMock.Object, disabledMock.Object], NullLogger<AlertDispatcher>.Instance);
        var alert = new PatientAlertNotification(Guid.NewGuid(), "Пацієнт", VitalStatus.Critical, DateTime.UtcNow, []);

        // Act
        await dispatcher.DispatchAsync(alert);

        // Assert
        enabledMock.Verify(c => c.SendAlertAsync(alert, It.IsAny<CancellationToken>()), Times.Once);
        disabledMock.Verify(c => c.SendAlertAsync(It.IsAny<PatientAlertNotification>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task DispatchAsync_WhenOneChannelThrows_OtherChannelsStillExecute()
    {
        // Arrange
        var failingMock = new Mock<IAlertChannel>();
        failingMock.SetupGet(c => c.Name).Returns("FailingChannel");
        failingMock.SetupGet(c => c.IsEnabled).Returns(true);
        failingMock.Setup(c => c.SendAlertAsync(It.IsAny<PatientAlertNotification>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Network down"));

        var healthyMock = new Mock<IAlertChannel>();
        healthyMock.SetupGet(c => c.Name).Returns("HealthyChannel");
        healthyMock.SetupGet(c => c.IsEnabled).Returns(true);

        var dispatcher = new AlertDispatcher([failingMock.Object, healthyMock.Object], NullLogger<AlertDispatcher>.Instance);
        var alert = new PatientAlertNotification(Guid.NewGuid(), "Пацієнт", VitalStatus.Critical, DateTime.UtcNow, []);

        // Act
        await dispatcher.DispatchAsync(alert);

        // Assert
        failingMock.Verify(c => c.SendAlertAsync(alert, It.IsAny<CancellationToken>()), Times.Once);
        healthyMock.Verify(c => c.SendAlertAsync(alert, It.IsAny<CancellationToken>()), Times.Once);
    }
}
