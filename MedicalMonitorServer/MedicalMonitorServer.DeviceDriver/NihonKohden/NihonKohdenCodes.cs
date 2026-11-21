namespace MedicalMonitorServer.DeviceDriver.NihonKohden;

/// <summary>
/// Nihon Kohden's parameter codes, the same over both of the monitor's protocols: HL7 sends
/// them as six digits in OBX-3 ("007000"), LS-NET as two bytes (07 00), each byte being three
/// of those digits. Seen on a live BSM-3562 (SW 07-41); a code it did not send is not here.
/// </summary>
internal static class NihonKohdenCodes
{
    /// <summary>Code -> Mindray parameter id of the same measurement (what the feed reads).</summary>
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
        ["007000"] = (160, "SpO2"),
        ["007001"] = (161, "Pulse Rate (PR)"),
        ["009000"] = (170, "NIBP Systolic"),
        ["009001"] = (171, "NIBP Diastolic"),
        ["009002"] = (172, "NIBP Mean"),
        ["044006"] = (600, "Pulse Rate (PR)"),      // from the arterial line
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
        : IsTemperature(code) ? 0.1                                 // °C
        : 1;
}
