using System.Text.Json;
using System.Text.Json.Serialization;
using MedicalMonitorServer.Contracts.Api;
using MedicalMonitorServer.Core.Monitoring;

namespace MedicalMonitorServer.Core.Tests;

public class VitalReadingsTests
{
    [Fact]
    public void ToDto_MissingValues_AreNull()
    {
        var dto = new VitalReadings(HeartRate: 72).ToDto();

        Assert.Equal(72, dto.HeartRate);
        Assert.Null(dto.SpO2);
        Assert.Null(dto.RespiratoryRate);
        Assert.Null(dto.Temperature);
        Assert.Null(dto.PulseRate);
        Assert.NotNull(dto.Nibp);
        Assert.Null(dto.Nibp!.Systolic);
        Assert.Null(dto.Nibp.Diastolic);
        Assert.Null(dto.Nibp.MeanArterialPressure);
    }

    [Fact]
    public void ToDto_ComputesMap_OnlyWhenBothPressuresKnown()
    {
        Assert.Equal(93, new VitalReadings(Systolic: 120, Diastolic: 80).ToDto().Nibp!.MeanArterialPressure);
        Assert.Null(new VitalReadings(Systolic: 120).ToDto().Nibp!.MeanArterialPressure);
    }

    [Fact]
    public void From_TreatsZeroAndNullAsNotMeasured()
    {
        var r = VitalReadings.From(new VitalsDto(0, null, new BloodPressureDto(0, null, null), 16, 0, null));

        Assert.Equal(new VitalReadings(RespiratoryRate: 16), r);
    }

    [Fact]
    public void From_AcceptsMissingNibp()
    {
        var r = VitalReadings.From(new VitalsDto(60, 97, null, null, 36.6, null));

        Assert.Equal(new VitalReadings(HeartRate: 60, SpO2: 97, Temperature: 36.6), r);
    }

    [Fact]
    public void Json_SendsNullNotZero()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter());

        var json = JsonSerializer.Serialize(new VitalReadings(HeartRate: 72).ToDto(), options);

        Assert.Contains("\"spO2\":null", json);
        Assert.Contains("\"temperature\":null", json);
        Assert.DoesNotContain(":0", json);
    }
}

public class AlertStateServiceTests
{
    private const string Patient = "P-1";

    private static AlertDto Device(int code, DeviceAlarmLevel level, AlertCategory category, string text = "alarm") =>
        new(AlertDto.BuildDeviceId(Patient, code), Patient, null, AlertDto.SeverityOf(level), text, null,
            DateTimeOffset.UtcNow, Source: AlertSource.Device, Code: code, Level: level, Category: category);

    [Fact]
    public void ThresholdAlert_HasThresholdSourceAndNoDeviceFields()
    {
        var a = Assert.Single(AlertEvaluator.Evaluate(Patient, new VitalReadings(HeartRate: 30)));

        Assert.Equal(AlertSource.Threshold, a.Source);
        Assert.Equal(VitalSignType.HeartRate, a.VitalSign);
        Assert.Equal(30, a.Value);
        Assert.Null(a.Code);
        Assert.Null(a.Level);
    }

    [Fact]
    public void ReconcileDevice_RaisesNewAndClearsMissing()
    {
        var svc = new AlertStateService();

        var first = svc.ReconcileDevice(Patient, AlertCategory.Technical, new[]
        {
            Device(458, DeviceAlarmLevel.Low, AlertCategory.Technical, "ECG lead off"),
            Device(5, DeviceAlarmLevel.Medium, AlertCategory.Technical),
        });
        Assert.Equal(2, first.Raised.Count);
        var leadOff = first.Raised.Single(a => a.Code == 458);
        Assert.Equal("P-1:device:458", leadOff.AlertId);
        Assert.Equal("ECG lead off", leadOff.Message);
        Assert.Equal(AlertSeverity.Warning, leadOff.Severity);
        Assert.Equal(AlertSource.Device, leadOff.Source);

        // Unchanged list: nothing to broadcast.
        Assert.False(svc.ReconcileDevice(Patient, AlertCategory.Technical, new[]
        {
            Device(458, DeviceAlarmLevel.Low, AlertCategory.Technical),
            Device(5, DeviceAlarmLevel.Medium, AlertCategory.Technical),
        }).HasChanges);

        // Empty list = no technical alarms any more.
        var cleared = svc.ReconcileDevice(Patient, AlertCategory.Technical, Array.Empty<AlertDto>());
        Assert.Equal(2, cleared.Cleared.Count);
        Assert.Empty(svc.GetActiveAlerts());
    }

    [Fact]
    public void ReconcileDevice_PriorityChange_ReAlarms()
    {
        var svc = new AlertStateService();
        svc.ReconcileDevice(Patient, AlertCategory.Physiological, new[] { Device(10002, DeviceAlarmLevel.Medium, AlertCategory.Physiological) });
        svc.Acknowledge("P-1:device:10002");

        var changes = svc.ReconcileDevice(Patient, AlertCategory.Physiological, new[] { Device(10002, DeviceAlarmLevel.High, AlertCategory.Physiological) });

        var a = Assert.Single(changes.Raised);
        Assert.Equal(AlertSeverity.Critical, a.Severity);
        Assert.Equal(DeviceAlarmLevel.High, a.Level);
        Assert.False(a.IsAcknowledged);
    }

    [Fact]
    public void Categories_AndThresholds_DoNotClearEachOther()
    {
        var svc = new AlertStateService();
        svc.ReconcileDevice(Patient, AlertCategory.Physiological, new[] { Device(10002, DeviceAlarmLevel.High, AlertCategory.Physiological) });
        svc.ReconcileDevice(Patient, AlertCategory.Technical, new[] { Device(458, DeviceAlarmLevel.Low, AlertCategory.Technical) });
        svc.Reconcile(Patient, new VitalReadings(HeartRate: 30));

        Assert.False(svc.ReconcileDevice(Patient, AlertCategory.Technical, new[] { Device(458, DeviceAlarmLevel.Low, AlertCategory.Technical) }).HasChanges);

        var thresholdCleared = svc.Reconcile(Patient, new VitalReadings(HeartRate: 70));
        Assert.Equal("P-1:HeartRate", Assert.Single(thresholdCleared.Cleared).AlertId);

        Assert.Equal(new[] { 458, 10002 }, svc.GetActiveAlerts().Select(a => a.Code!.Value).OrderBy(c => c));
    }
}

public class PatientRegistryWaveformTests
{
    private static WaveformChannelDto Channel(int id, string label, WaveformType? type) =>
        new(id, type, label, 1.0, "", 128, 256, 0, 255);

    [Fact]
    public void Channels_AreMergedById_AndDroppedOnDischarge()
    {
        var reg = new PatientRegistry();
        reg.Upsert(new PatientInfoDto("P-1", "Test", 1, null));

        Assert.Empty(reg.GetWaveformChannels("P-1"));

        reg.UpdateWaveformChannels("P-1", new[] { Channel(1152, "Pleth", WaveformType.Pleth) });
        reg.UpdateWaveformChannels("P-1", new[] { Channel(1101, "II", WaveformType.ECG_II) });
        reg.UpdateWaveformChannels("P-1", new[] { Channel(1101, "I", WaveformType.ECG_I) });

        var channels = reg.GetWaveformChannels("P-1");
        Assert.Equal(new[] { 1101, 1152 }, channels.Select(c => c.ChannelId));
        Assert.Equal(WaveformType.ECG_I, channels[0].Type);

        reg.Remove("P-1");
        Assert.Empty(reg.GetWaveformChannels("P-1"));
    }
}
