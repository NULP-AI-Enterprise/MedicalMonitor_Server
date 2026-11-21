using System.Globalization;
using System.Net.Sockets;
using System.Text;

namespace MedicalMonitorServer.DeviceDriver.NihonKohden;

/// <summary>
/// The ADT messages a Nihon Kohden monitor takes when "USE ADT" is on in its HL7 settings.
/// Worked out on a live BSM-3562 (SW 07-41) on 2026-11-21; the Administrator's Guide only
/// names the ports:
/// <list type="bullet">
/// <item>admit port (9005): ADT^A04 admits a patient, replacing whoever was admitted. ADT^A02
/// discharges - whatever id it names - so it is deliberately not offered here.</item>
/// <item>patient information port (9006): ADT^A08 rewrites the id and details of the admitted
/// patient and keeps the monitor's data. Refused when nobody is admitted.</item>
/// </list>
/// Every message needs PID and a PV1 whose PV1-3 carries the monitor's own bed name
/// (<c>^^ICU-001</c>); without it the answer is MSA|AE. EVN is not needed. The monitor keeps
/// the first 16 characters of PID-3 and never sends a name back, in any message.
/// </summary>
internal static class NihonKohdenAdt
{
    /// <summary>What the monitor stores of PID-3; a longer id would come back cut short and match nobody.</summary>
    public const int MaxPatientIdLength = 16;

    public static string Admit(string bedName, string patientId, string patientName, string controlId, DateTime now) =>
        Build("ADT^A04^ADT_A01", bedName, patientId, patientName, controlId, now);

    public static string Update(string bedName, string patientId, string patientName, string controlId, DateTime now) =>
        Build("ADT^A08^ADT_A01", bedName, patientId, patientName, controlId, now);

    /// <summary>Null when the monitor can hold the id unchanged, else why it cannot.</summary>
    public static string? WhyIdCannotBeStored(string patientId)
    {
        if (patientId.Length == 0) return "it is empty";
        if (patientId.Length > MaxPatientIdLength)
            return $"it is longer than the {MaxPatientIdLength} characters the monitor keeps";
        if (patientId.Any(c => c is < ' ' or > '~' or '|' or '^' or '~' or '\\' or '&'))
            return "it has characters other than plain ASCII letters, digits and punctuation";
        return null;
    }

    /// <summary>True for an acknowledgement with MSA-1 = AA.</summary>
    public static bool IsAccepted(string acknowledgement) =>
        NihonKohdenParser.Field(acknowledgement, "MSA", 1) == "AA";

    private static string Build(string type, string bedName, string patientId, string patientName, string controlId, DateTime now) =>
        string.Join('|', "MSH", "^~\\&", "CLIENT APP", "CLIENT FACILITY", "NIHON KOHDEN", "NIHON KOHDEN",
            now.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture), "", type, controlId, "P", "2.4") + "\r"
        + $"PID|||{patientId}||{ToName(patientName)}\r"
        + $"PV1||I|^^{bedName}\r";

    /// <summary>
    /// "Smith Anna Maria" -> "Smith^Anna Maria" (family ^ given). A name that is not plain ASCII
    /// is left out: the monitor acknowledges Cyrillic bytes, but what it then shows on its
    /// screen has not been checked, and the roster has the name anyway.
    /// </summary>
    private static string ToName(string patientName)
    {
        var name = patientName.Trim();
        if (name.Length == 0 || name.Any(c => c is < ' ' or > '~' or '|' or '^' or '~' or '\\' or '&')) return "";

        int space = name.IndexOf(' ');
        return space < 0 ? name : $"{name[..space]}^{name[(space + 1)..].Trim()}";
    }

    /// <summary>
    /// One request, one answer, on a connection of its own: the monitor's ADT ports are not
    /// streams. Returns the answer without MLLP framing; null when none came in time.
    /// </summary>
    public static async Task<string?> ExchangeAsync(
        string ip, int port, string message, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);

        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(ip, port, timeoutCts.Token);
            using var stream = client.GetStream();

            await stream.WriteAsync(ComunicationService.Frame(Encoding.ASCII.GetBytes(message)), timeoutCts.Token);

            var framing = new ComunicationService();
            var buffer = new byte[4096];
            while (true)
            {
                int read = await stream.ReadAsync(buffer, timeoutCts.Token);
                if (read == 0) return null;

                var frames = framing.Feed(buffer.AsSpan(0, read));
                if (frames.Count > 0) return Encoding.ASCII.GetString(frames[0]);
            }
        }
        catch (Exception ex) when (ex is SocketException or IOException ||
                                   (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            return null;
        }
    }
}
