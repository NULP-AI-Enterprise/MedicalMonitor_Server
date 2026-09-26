namespace MedicalMonitorServer.Contracts.Models;

public enum PatientType
{
    Unknown = 0,
    Adult,
    Pediatric,
    Neonate,
}

/// <summary>Patient demographics as admitted on the monitor (ORU msg 103 or the ADT beacon: PID, PV1, EVN, OBX 51/52/2301-2308).</summary>
public sealed class PatientInfo
{
    /// <summary>PID-3, the monitor's GUID for the patient.</summary>
    public string PatientId { get; set; } = "";

    /// <summary>PID-5 first component.</summary>
    public string LastName { get; set; } = "";

    /// <summary>PID-5 second component.</summary>
    public string FirstName { get; set; } = "";

    public string FullName { get; set; } = "";

    /// <summary>PID-7.</summary>
    public DateOnly? DateOfBirth { get; set; }

    /// <summary>PID-8: "M", "F" or empty.</summary>
    public string Sex { get; set; } = "";

    /// <summary>PV1-2, e.g. "I" = inpatient.</summary>
    public string PatientClass { get; set; } = "";

    /// <summary>PV1-18: A / P / N / U.</summary>
    public PatientType PatientType { get; set; }

    /// <summary>PV1-3 department subcomponent.</summary>
    public string Department { get; set; } = "";

    /// <summary>OBX 2301, falling back to the PV1-3 bed subcomponent.</summary>
    public string BedNumber { get; set; } = "";

    /// <summary>OBX 2308 ("BedNoStr").</summary>
    public string BedLabel { get; set; } = "";

    /// <summary>The monitor's own address, from PV1-3.</summary>
    public string MonitorIp { get; set; } = "";

    public int? MonitorPort { get; set; }

    /// <summary>PV1-3 admitted flag.</summary>
    public bool? Admitted { get; set; }

    /// <summary>EVN-2 of the ADT beacon; null when "00000000" (nobody admitted).</summary>
    public DateOnly? AdmitDate { get; set; }

    /// <summary>OBX 52.</summary>
    public double? HeightCm { get; set; }

    /// <summary>OBX 51.</summary>
    public double? WeightKg { get; set; }

    /// <summary>OBX 2302, text component (e.g. "N" = not set).</summary>
    public string BloodType { get; set; } = "";

    /// <summary>OBX 2303: pacemaker on/off.</summary>
    public bool? Paced { get; set; }
}
