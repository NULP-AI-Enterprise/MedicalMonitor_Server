namespace MedicalMonitorServer.Contracts.Models;

/// <summary>
/// One OBX segment as sent by the monitor, text fields decoded. Every OBX of
/// a message is kept here, whether or not the parser interpreted it.
/// </summary>
public sealed class UmecObservation
{
    /// <summary>OBX-2: NM, CE, ST, TX, CD.</summary>
    public string ValueType { get; set; } = "";

    /// <summary>OBX-3 first component: parameter id or Mindray attribute code (2002, 2025, ...).</summary>
    public string Code { get; set; } = "";

    /// <summary>OBX-3 second component, decoded.</summary>
    public string Label { get; set; } = "";

    /// <summary>OBX-4: the parameter / module the observation refers to.</summary>
    public string SubId { get; set; } = "";

    /// <summary>OBX-5 as sent (components still separated by '^').</summary>
    public string Value { get; set; } = "";

    /// <summary>OBX-5 first component.</summary>
    public string ValueCode { get; set; } = "";

    /// <summary>OBX-5 second component, decoded.</summary>
    public string ValueText { get; set; } = "";

    /// <summary>OBX-6.</summary>
    public string Units { get; set; } = "";

    /// <summary>OBX-11, normally "F".</summary>
    public string Status { get; set; } = "";

    /// <summary>OBX-13, e.g. "APERIODIC".</summary>
    public string AccessCheck { get; set; } = "";

    /// <summary>OBX-14, monitor local time.</summary>
    public DateTime? Timestamp { get; set; }
}
