using MedicalMonitorServer.Core.Monitoring;
using MedicalMonitorServer.DeviceDriver;

namespace MedicalMonitorServer.Services;

/// <summary>What became of a station's request to start or stop a patient's cuff measurement.</summary>
public enum NibpRequestOutcome
{
    /// <summary>The command is on its way to the patient's monitor.</summary>
    Accepted,

    PatientNotFound,

    /// <summary>The patient is on the roster and on no monitor.</summary>
    NoMonitor,

    /// <summary>The patient's monitor is not one whose cuff can be started over the network.</summary>
    NotSupported,

    /// <summary>It could be, and the server has not been allowed to (NihonKohden:LsNet:RemoteNibp).</summary>
    NotAllowed,

    /// <summary>The monitor is not sending data, and takes no command while it is not.</summary>
    NotStreaming,

    /// <summary>A start while the monitor reports a measurement running.</summary>
    AlreadyMeasuring,

    /// <summary>A start a moment after another one.</summary>
    TooSoon,
}

/// <param name="MonitorIp">The patient's monitor; null when the request did not get as far as one.</param>
public readonly record struct NibpRequestResult(NibpRequestOutcome Outcome, string? MonitorIp)
{
    /// <summary>What a station shows its user when the request was not accepted; null when it was.</summary>
    public string? Refusal => Outcome switch
    {
        NibpRequestOutcome.Accepted => null,
        NibpRequestOutcome.PatientNotFound => "There is no such patient.",
        NibpRequestOutcome.NoMonitor => "The patient is not on a monitor.",
        NibpRequestOutcome.NotSupported => "This patient's monitor cannot be told to measure NIBP from a station.",
        NibpRequestOutcome.NotAllowed => "Starting NIBP from a station is turned off on this server.",
        NibpRequestOutcome.NotStreaming => "The monitor is not sending data.",
        NibpRequestOutcome.AlreadyMeasuring => "A measurement is already running.",
        NibpRequestOutcome.TooSoon => "A measurement was asked for a moment ago. Wait for the monitor to start it.",
        _ => "The request could not be passed on.",
    };
}

/// <summary>
/// Starts and stops a patient's cuff (NIBP) measurement on behalf of a station: finds the
/// monitor the patient is on and hands the request to its driver. Every request is written to
/// the log with its outcome and where it came from - the log is the record of who had a cuff
/// inflated and when. Nothing is sent twice: what the monitor made of a command shows in the
/// vitals it reports next (<c>nibp.measuring</c>).
/// </summary>
public sealed class NibpControl
{
    private readonly IPatientRegistry _patients;
    private readonly IMonitorBindings _bindings;
    private readonly DeviceService _devices;
    private readonly ILogger<NibpControl> _logger;

    public NibpControl(IPatientRegistry patients, IMonitorBindings bindings, DeviceService devices, ILogger<NibpControl> logger)
    {
        _patients = patients;
        _bindings = bindings;
        _devices = devices;
        _logger = logger;
    }

    /// <param name="start">True to start a measurement, false to stop the one that is running.</param>
    /// <param name="requestedFrom">Address the request came from, for the log.</param>
    public NibpRequestResult Request(string patientId, bool start, string? requestedFrom = null)
    {
        var result = Decide(patientId, start);

        _logger.Log(
            result.Outcome == NibpRequestOutcome.Accepted ? LogLevel.Information : LogLevel.Warning,
            "NIBP {Action} for patient {PatientId} on monitor {MonitorIp}, asked from {From}: {Outcome}.",
            start ? "start" : "stop", patientId, result.MonitorIp ?? "(none)", requestedFrom ?? "(unknown)", result.Outcome);

        return result;
    }

    private NibpRequestResult Decide(string patientId, bool start)
    {
        if (_patients.Get(patientId) is null) return new(NibpRequestOutcome.PatientNotFound, null);

        if (_bindings.FindMonitorOf(patientId) is not { } monitorIp) return new(NibpRequestOutcome.NoMonitor, null);

        var outcome = _devices.RequestCuff(monitorIp, start) switch
        {
            CuffRequestStatus.Accepted => NibpRequestOutcome.Accepted,
            CuffRequestStatus.NotAllowed => NibpRequestOutcome.NotAllowed,
            CuffRequestStatus.NotStreaming => NibpRequestOutcome.NotStreaming,
            CuffRequestStatus.AlreadyMeasuring => NibpRequestOutcome.AlreadyMeasuring,
            CuffRequestStatus.TooSoon => NibpRequestOutcome.TooSoon,
            _ => NibpRequestOutcome.NotSupported,
        };

        return new(outcome, monitorIp);
    }
}
