namespace MedicalMonitorServer.Dal.History;

/// <summary>
/// A row of the <c>patients</c> table: somebody a station admitted, on a monitor now or
/// discharged since. One row however many times they were admitted: see <see cref="RecordedAdmission"/>.
/// </summary>
/// <param name="Id">
/// The patient's id, a UUID made up at their first admission. It is what the rest of the system
/// knows them by, and what a station gives to admit them again into the history they have.
/// </param>
/// <param name="Num">
/// Short number the per-second rows refer to the patient by, so that a 36-character id is not
/// repeated 86 400 times a day. It means something in this file only.
/// </param>
/// <param name="DrAlexId">What the hospital's DrAlex system calls the patient, as typed in at a station; not unique.</param>
/// <param name="MonitorIp">Monitor the patient is on; null while they have none.</param>
/// <param name="AdmittedAt">When they were admitted last: the stay that goes on, or the one that ended last.</param>
/// <param name="DischargedAt">Null while the patient is being recorded.</param>
/// <param name="LastRecordedAt">Newest recorded second, Unix time; filled in by the listing queries only.</param>
/// <param name="StandingCuff">
/// The cuff reading that stood on the patient's monitor when they were admitted, or moved to it:
/// taken before, of whoever, so not theirs. A monitor shows a reading until the next one, and
/// this is how the recording tells, after a restart as well, that it is not a new one.
/// Null when the monitor showed none.
/// </param>
/// <param name="StandingSince">When <paramref name="StandingCuff"/> was noted, Unix time: the last admission, or the last move.</param>
public sealed record RecordedPatient(
    string Id,
    int Num,
    string? DrAlexId,
    string? FullName,
    int BedNumber,
    string PatientType,
    string? Diagnosis,
    string? MonitorIp,
    DateTimeOffset AdmittedAt,
    DateTimeOffset? DischargedAt,
    long? LastRecordedAt = null,
    CuffReading? StandingCuff = null,
    long? StandingSince = null);

/// <summary>What is known about a patient at the moment a station admits them.</summary>
/// <param name="StandingCuff">The cuff reading on the monitor at that moment; see <see cref="RecordedPatient.StandingCuff"/>.</param>
public sealed record NewRecordedPatient(
    string Id,
    string? DrAlexId,
    string? FullName,
    int BedNumber,
    string PatientType,
    string? Diagnosis,
    string MonitorIp,
    DateTimeOffset AdmittedAt,
    CuffReading? StandingCuff = null);

/// <summary>
/// A row of the <c>admissions</c> table: one stay of a patient, from a station admitting them
/// to a station discharging them. Somebody who comes back is admitted again under the id they
/// have and recorded into the same history, so a patient may have several; between two of
/// them nothing was recorded.
/// </summary>
/// <param name="DischargedAt">Null while the stay goes on.</param>
/// <param name="BedNumber">The bed they were on, as last entered during the stay.</param>
/// <param name="MonitorIp">The monitor they were on, as last known during the stay; null when it was not kept.</param>
public sealed record RecordedAdmission(DateTimeOffset AdmittedAt, DateTimeOffset? DischargedAt, int BedNumber, string? MonitorIp);

/// <summary>A cuff (NIBP) reading.</summary>
/// <param name="MeasuredAt">When the monitor says it was taken, Unix seconds; null when it does not say.</param>
public sealed record CuffReading(int? Systolic, int? Diastolic, int? Mean, long? MeasuredAt);

/// <summary>A cuff reading as it is in the history.</summary>
/// <param name="Timestamp">The second it was recorded in, Unix time.</param>
public sealed record RecordedCuff(CuffReading Reading, long Timestamp);

/// <summary>A row of the <c>vitals</c> table: one patient in one second.</summary>
/// <param name="Timestamp">The second, Unix time (UTC).</param>
/// <param name="Cuff">Set in the second a new cuff reading appeared, null in every other.</param>
/// <param name="Extra">Every other value as a JSON object, code to number: {"art.sys":118,"ecg.st.ii":0.05}. Null when there is none.</param>
public sealed record VitalsRow(
    int PatientNum,
    long Timestamp,
    int? HeartRate,
    int? SpO2,
    int? PulseRate,
    int? RespiratoryRate,
    double? Temperature,
    CuffReading? Cuff,
    string? Extra);
