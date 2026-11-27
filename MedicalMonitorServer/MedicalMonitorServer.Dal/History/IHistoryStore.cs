namespace MedicalMonitorServer.Dal.History;

/// <summary>
/// Where the patients a station admitted and their per-second values are kept. Every method
/// throws when the database cannot be read or written: what to do about it - refuse an
/// admission, carry on monitoring without recording - is for the caller to decide.
/// </summary>
public interface IHistoryStore : IDisposable
{
    /// <summary>Adds the patient, gives them the next number and begins their first stay.</summary>
    RecordedPatient Admit(NewRecordedPatient patient);

    /// <summary>
    /// Admits a discharged patient again, on this monitor and bed. They keep their id and their
    /// number, so what is recorded from now on continues the history they have; a new stay
    /// begins. Null, and nothing changed, when nobody has this id or they are admitted now.
    /// </summary>
    /// <param name="standingCuff">The cuff reading that monitor shows at <paramref name="at"/>; see <see cref="RecordedPatient.StandingCuff"/>.</param>
    RecordedPatient? Readmit(string id, string monitorIp, int bedNumber, DateTimeOffset at, CuffReading? standingCuff);

    /// <summary>Replaces what a station entered about the patient.</summary>
    void Update(string id, string? drAlexId, string? fullName, int bedNumber, string patientType, string? diagnosis);

    /// <summary>
    /// Notes the monitor the patient is on now (null: none), and the cuff reading that monitor
    /// already showed at <paramref name="at"/>, which is not theirs.
    /// <paramref name="displacedId"/> is the admitted patient who was on that monitor and now has none.
    /// </summary>
    void MoveMonitor(string id, string? monitorIp, string? displacedId, CuffReading? standingCuff, DateTimeOffset at);

    /// <summary>
    /// Ends the patient's recording and the stay that was going on. False when they are not
    /// being recorded (unknown, or discharged already).
    /// </summary>
    bool Discharge(string id, DateTimeOffset at);

    /// <summary>Everybody admitted and not discharged, in the order they were admitted.</summary>
    IReadOnlyList<RecordedPatient> GetActive();

    /// <summary>Recorded patients, the last admitted first; only those with this DrAlex id when one is given.</summary>
    IReadOnlyList<RecordedPatient> GetPatients(string? drAlexId, int limit);

    /// <summary>
    /// Discharged patients, the last discharged first; only those with this DrAlex id when one
    /// is given. Somebody admitted again is not among them until they are discharged again.
    /// </summary>
    IReadOnlyList<RecordedPatient> GetDischarged(string? drAlexId, int limit);

    RecordedPatient? GetPatient(string id);

    /// <summary>
    /// The stays of these patients, by <see cref="RecordedPatient.Num"/>, each patient's in the
    /// order they happened. Every recorded patient has at least one.
    /// </summary>
    IReadOnlyDictionary<int, IReadOnlyList<RecordedAdmission>> GetAdmissions(IReadOnlyCollection<int> patientNums);

    /// <summary>Writes the rows of one second, all or none.</summary>
    void Append(IReadOnlyList<VitalsRow> rows);

    /// <summary>
    /// The newest <paramref name="limit"/> rows of the patient between two seconds (both
    /// included), oldest first.
    /// </summary>
    IReadOnlyList<VitalsRow> GetVitals(int patientNum, long from, long to, int limit);

    /// <summary>The cuff reading recorded last for the patient, and when; null when none was.</summary>
    RecordedCuff? GetLastCuff(int patientNum);

    /// <summary>Size of the database on disk, in bytes.</summary>
    long SizeBytes { get; }
}
