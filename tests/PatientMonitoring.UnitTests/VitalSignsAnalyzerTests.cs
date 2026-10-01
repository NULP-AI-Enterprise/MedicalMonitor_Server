using Microsoft.Extensions.Options;
using PatientMonitoring.Api.Models;
using PatientMonitoring.Api.Services;

namespace PatientMonitoring.UnitTests;

public class VitalSignsAnalyzerTests
{
    private readonly VitalSignsAnalyzer _analyzer;

    public VitalSignsAnalyzerTests()
    {
        var options = Options.Create(new VitalThresholds());
        _analyzer = new VitalSignsAnalyzer(options);
    }

    [Fact]
    public void Analyze_WhenAllVitalsAreNormal_ReturnsNormalStatusAndNoAlerts()
    {
        // Arrange
        var vitals = new VitalSign
        {
            HeartRate = 72,
            SystolicBloodPressure = 120,
            DiastolicBloodPressure = 80,
            OxygenSaturation = 98.0,
            Temperature = 36.6,
            RespiratoryRate = 16
        };

        // Act
        var result = _analyzer.Analyze(vitals);

        // Assert
        Assert.Equal(VitalStatus.Normal, result.Status);
        Assert.Empty(result.Alerts);
    }

    [Fact]
    public void Analyze_WhenHeartRateExceedsWarning_ReturnsWarning()
    {
        // Arrange (Threshold: WarningHigh = 110, CriticalHigh = 130)
        var vitals = new VitalSign { HeartRate = 115 };

        // Act
        var result = _analyzer.Analyze(vitals);

        // Assert
        Assert.Equal(VitalStatus.Warning, result.Status);
        Assert.Single(result.Alerts);
        Assert.Equal("HeartRate", result.Alerts[0].Parameter);
        Assert.Contains("Пульс", result.Alerts[0].Message);
    }

    [Fact]
    public void Analyze_WhenHeartRateExceedsCritical_ReturnsCritical()
    {
        // Arrange
        var vitals = new VitalSign { HeartRate = 135 };

        // Act
        var result = _analyzer.Analyze(vitals);

        // Assert
        Assert.Equal(VitalStatus.Critical, result.Status);
        Assert.Single(result.Alerts);
        Assert.Equal(VitalStatus.Critical, result.Alerts[0].Status);
    }

    [Fact]
    public void Analyze_WhenSpO2DropsBelowCritical_ReturnsCritical()
    {
        // Arrange (CriticalLow = 88)
        var vitals = new VitalSign { OxygenSaturation = 85.0 };

        // Act
        var result = _analyzer.Analyze(vitals);

        // Assert
        Assert.Equal(VitalStatus.Critical, result.Status);
        Assert.Single(result.Alerts);
        Assert.Equal("OxygenSaturation", result.Alerts[0].Parameter);
    }

    [Fact]
    public void Analyze_WhenMultipleViolationsOccur_ReturnsHighestSeverity()
    {
        // Arrange: Warning on HeartRate, Critical on Temperature
        var vitals = new VitalSign
        {
            HeartRate = 115,          // Warning
            Temperature = 40.0         // Critical (> 39.5)
        };

        // Act
        var result = _analyzer.Analyze(vitals);

        // Assert
        Assert.Equal(VitalStatus.Critical, result.Status);
        Assert.Equal(2, result.Alerts.Count);
    }

    [Fact]
    public void Analyze_WhenCustomThresholdsApplied_RespectsCustomLimits()
    {
        // Arrange: Custom thresholds
        var customThresholds = new VitalThresholds
        {
            HeartRate = new ThresholdRange { WarningHigh = 100, CriticalHigh = 120 }
        };
        var analyzer = new VitalSignsAnalyzer(Options.Create(customThresholds));
        var vitals = new VitalSign { HeartRate = 105 };

        // Act
        var result = analyzer.Analyze(vitals);

        // Assert
        Assert.Equal(VitalStatus.Warning, result.Status);
        Assert.Single(result.Alerts);
    }
}
