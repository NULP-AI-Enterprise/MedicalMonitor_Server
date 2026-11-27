using MedicalMonitorServer.Contracts.Models;

namespace MedicalMonitorServer.DeviceDriver.NihonKohden;

/// <summary>
/// Nihon Kohden's parameter codes, the same over both of the monitor's protocols: HL7 sends
/// them as six digits in OBX-3 ("007000"), LS-NET as two bytes (07 00), each byte being three
/// of those digits. Seen on a live BSM-3562 (SW 07-41); a code it did not send is not here.
/// </summary>
internal static class NihonKohdenCodes
{
    /// <summary>
    /// Code -> the parameter id the feed reads the measurement under: Mindray's id of the same
    /// measurement, or one of <see cref="ServerParameterIds"/> where Mindray's table has none.
    /// </summary>
    private static readonly Dictionary<string, (int ParameterId, string Name)> Parameters = new()
    {
        ["001000"] = (101, "Heart Rate (HR)"),
        ["002000"] = (102, "PVCs"),
        ["003000"] = (105, "ST-I"),
        ["003001"] = (106, "ST-II"),
        ["003002"] = (107, "ST-III"),
        ["003003"] = (108, "ST-aVR"),
        ["003004"] = (109, "ST-aVL"),
        ["003005"] = (110, "ST-aVF"),
        ["003006"] = (ServerParameterIds.StV1, "ST-V1"),
        ["003007"] = (ServerParameterIds.StV1 + 1, "ST-V2"),
        ["003008"] = (ServerParameterIds.StV1 + 2, "ST-V3"),
        ["003009"] = (ServerParameterIds.StV1 + 3, "ST-V4"),
        ["003010"] = (ServerParameterIds.StV1 + 4, "ST-V5"),
        ["003011"] = (ServerParameterIds.StV1 + 5, "ST-V6"),

        // The respiration rate. Both codes carried 12 while the monitor showed an impedance
        // rate of 12 (2026-11-22), and both come without a value while respiration is switched
        // off; what tells them apart was not found. The feed takes whichever has a value.
        ["004005"] = (151, "Respiration Rate (RR)"),
        ["005000"] = (151, "Respiration Rate (RR)"),
        ["007000"] = (160, "SpO2"),
        ["007001"] = (161, "Pulse Rate (PR)"),

        // The perfusion index in hundredths: 278 on the wire while the monitor's SpO2 window
        // showed "PI 2.78" (2026-11-22).
        ["007008"] = (162, "Perfusion Index (PI)"),
        ["009000"] = (170, "NIBP Systolic"),
        ["009001"] = (171, "NIBP Diastolic"),
        ["009002"] = (172, "NIBP Mean"),

        // No pulse rate of the cuff: the BSM-3562 was not seen to send one. With a reading on it
        // the monitor listed 009000-009002, 009008, 009010 and 009011 and nothing else of the
        // cuff (captures of 2026-11-21 and 2026-11-26). 009009 in particular is not it: the
        // parameter names of Nihon Kohden's central station software run "NIBP Meas Stat",
        // "NIBP Cuff", "NIBP Meas Mode", "NIBP Cuff Size", which is 009008 to 009011 as they
        // behave on the wire, so 009009 is the pressure in the cuff. That software has an
        // "NIBP/PR" too; its code goes here the day a monitor is seen to send it.
        ["044000"] = (ServerParameterIds.ArtSystolic, "ART Systolic"),
        ["044001"] = (ServerParameterIds.ArtDiastolic, "ART Diastolic"),
        ["044002"] = (ServerParameterIds.ArtMean, "ART Mean"),
        ["044006"] = (600, "Pulse Rate (PR)"),      // from the arterial line
        ["053000"] = (ServerParameterIds.CvpSystolic, "CVP Systolic"),
        ["053001"] = (ServerParameterIds.CvpDiastolic, "CVP Diastolic"),
        ["053002"] = (ServerParameterIds.CvpMean, "CVP Mean"),
    };

    /// <summary>What the monitor calls the parameters it was seen to send, for texts made here.</summary>
    private static readonly Dictionary<string, string> Labels = new()
    {
        ["001000"] = "HR",
        ["002000"] = "VPC",
        ["003000"] = "ST I", ["003001"] = "ST II", ["003002"] = "ST III",
        ["003003"] = "ST aVR", ["003004"] = "ST aVL", ["003005"] = "ST aVF",
        ["003006"] = "ST V1", ["003007"] = "ST V2", ["003008"] = "ST V3",
        ["003009"] = "ST V4", ["003010"] = "ST V5", ["003011"] = "ST V6",
        ["004005"] = "RR", ["005000"] = "RR",
        // Not passed on, named for the log. 004001 / 005001 count the seconds since the last
        // breath (0 to 4 and round again at 12 breaths a minute): the apnoea timer.
        ["004001"] = "Seconds since last breath", ["005001"] = "Seconds since last breath",
        ["007000"] = "SpO2",
        ["007001"] = "PR (SpO2)",
        ["007008"] = "PI",
        // 1 from the start of a cuff measurement to its end, failed or cancelled; 009010 is the
        // cuff mode, 0 manual and 1 at an interval (any length), with a 2 seen and not explained.
        ["009008"] = "NIBP measuring", ["009010"] = "NIBP mode",
        ["009000"] = "NIBP systolic", ["009001"] = "NIBP diastolic", ["009002"] = "NIBP mean",
        ["011000"] = "Skin temperature",
        ["014000"] = "Rectal temperature",
        ["044000"] = "ART systolic", ["044001"] = "ART diastolic", ["044002"] = "ART mean",
        ["044006"] = "PR (ART)",
        ["053000"] = "CVP systolic", ["053001"] = "CVP diastolic", ["053002"] = "CVP mean",
    };

    /// <summary>The monitor's name for a parameter; the code itself for one never seen named.</summary>
    public static string Label(string code) => Labels.TryGetValue(code, out var label) ? label : $"Parameter {code}";

    /// <summary>Whether <see cref="Label"/> has a name for the code, and not just the code back.</summary>
    public static bool IsNamed(string code) => Labels.ContainsKey(code);

    /// <summary>
    /// The sensor a parameter is measured by, as a technical alarm names it. One sensor feeds
    /// several parameters - an arterial line its systolic, diastolic and mean - and when it is
    /// off every one of them says so: that is one alarm about the sensor, not three.
    /// <para>
    /// 027000 and 028000 are taken to be the monitor's two temperature inputs: they appeared,
    /// without a value and in technical alarm, exactly when the probes were unplugged and the
    /// two temperatures (011000, 014000) left the list (2026-11-22). Not confirmed against the
    /// monitor's own wording.
    /// </para>
    /// </summary>
    public static string Sensor(string code) => (code.Length >= 3 ? code[..3] : code) switch
    {
        "001" or "002" or "003" => "ECG",
        "004" or "005" => "Respiration",
        "007" => "SpO2",
        "009" => "NIBP",
        "011" => "Skin temperature",
        "014" => "Rectal temperature",
        "027" => "Temperature 1",
        "028" => "Temperature 2",
        "044" => "ART",
        "053" => "CVP",
        _ => Label(code),
    };

    /// <summary>
    /// What a technical alarm on a sensor says, from the message code the monitor sends with
    /// it (the wire carries no text). Two codes are known:
    /// 04 came on the cuff while the monitor showed "NIBP connector off", and comes on every
    /// other sensor that is unplugged, so it is read as "connector off" for all of them;
    /// 13 came on the cuff when a measurement ended without a result.
    /// Any other code says what is certain - the sensor needs looking at - and keeps its
    /// number, so that it can be matched to the monitor's screen and named.
    /// </summary>
    public static string TechnicalAlarmText(string sensor, byte message) => message switch
    {
        0x04 => $"{sensor} connector off",
        0x13 when sensor == "NIBP" => "NIBP measurement failed",
        _ => $"{sensor}: check sensor ({message:X2})",
    };

    /// <summary>
    /// Mindray ids for the first and second temperature of a report. The monitor has a code
    /// per probe site, so sites are numbered in the order they are sent.
    /// </summary>
    public static readonly (int ParameterId, string Name)[] Temperatures =
    {
        (200, "Temperature T1"),
        (201, "Temperature T2"),
    };

    /// <summary>The temperature sites seen so far: 011000 skin, 014000 rectal.</summary>
    public static bool IsTemperature(string code) => code is "011000" or "014000";

    /// <summary>NIBP is a measurement taken now and then, not a running value.</summary>
    public static bool IsAperiodic(string code) => code.StartsWith("009", StringComparison.Ordinal);

    public static bool TryGetParameter(string code, out (int ParameterId, string Name) parameter) =>
        Parameters.TryGetValue(code, out parameter);

    /// <summary>The six-digit form of an LS-NET two-byte code: (0x2C, 0x06) -> "044006".</summary>
    public static string FromBytes(byte high, byte low) => $"{high:D3}{low:D3}";

    /// <summary>
    /// What one count of an LS-NET value is worth. The wire carries whole numbers and no
    /// scale; these are the ones that matched the monitor's screen and its HL7 output.
    /// </summary>
    public static double LsNetScale(string code) =>
        code.StartsWith("003", StringComparison.Ordinal) ? 0.01     // ST, mV
        : code == "007008" ? 0.01                                   // perfusion index, %
        : IsTemperature(code) ? 0.1                                 // °C
        : 1;
}
