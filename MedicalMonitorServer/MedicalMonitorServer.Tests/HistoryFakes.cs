using MedicalMonitorServer.Dal.History;

namespace MedicalMonitorServer.Tests;

/// <summary>A clock a test sets by hand: the recorder asks it when a monitor's message came in.</summary>
internal sealed class TestClock : TimeProvider
{
    public TestClock(DateTimeOffset now) => Now = now;

    public DateTimeOffset Now { get; set; }

    public override DateTimeOffset GetUtcNow() => Now;
}

/// <summary>
/// A history database that can be made to fail, on top of a real one: a full disk, a file
/// somebody locked. What a test switches on throws; everything else goes through.
/// </summary>
internal sealed class FlakyHistoryStore : IHistoryStore
{
    private readonly IHistoryStore _inner;

    public FlakyHistoryStore(IHistoryStore inner) => _inner = inner;

    public bool FailAdmit { get; set; }

    public bool FailUpdate { get; set; }

    public bool FailDischarge { get; set; }

    public bool FailAppend { get; set; }

    /// <summary>How many times rows were handed over, written or not.</summary>
    public int Appends { get; private set; }

    private static void ThrowIf(bool fail)
    {
        if (fail) throw new IOException("The disk is full.");
    }

    public RecordedPatient Admit(NewRecordedPatient patient)
    {
        ThrowIf(FailAdmit);
        return _inner.Admit(patient);
    }

    public RecordedPatient? Readmit(string id, string monitorIp, int bedNumber, DateTimeOffset at, CuffReading? standingCuff)
    {
        ThrowIf(FailAdmit);
        return _inner.Readmit(id, monitorIp, bedNumber, at, standingCuff);
    }

    public void Update(string id, string? drAlexId, string? fullName, int bedNumber, string patientType, string? diagnosis)
    {
        ThrowIf(FailUpdate);
        _inner.Update(id, drAlexId, fullName, bedNumber, patientType, diagnosis);
    }

    public void MoveMonitor(string id, string? monitorIp, string? displacedId, CuffReading? standingCuff, DateTimeOffset at) =>
        _inner.MoveMonitor(id, monitorIp, displacedId, standingCuff, at);

    public bool Discharge(string id, DateTimeOffset at)
    {
        ThrowIf(FailDischarge);
        return _inner.Discharge(id, at);
    }

    public IReadOnlyList<RecordedPatient> GetActive() => _inner.GetActive();

    public IReadOnlyList<RecordedPatient> GetPatients(string? drAlexId, int limit) => _inner.GetPatients(drAlexId, limit);

    public IReadOnlyList<RecordedPatient> GetDischarged(string? drAlexId, int limit) => _inner.GetDischarged(drAlexId, limit);

    public RecordedPatient? GetPatient(string id) => _inner.GetPatient(id);

    public IReadOnlyDictionary<int, IReadOnlyList<RecordedAdmission>> GetAdmissions(IReadOnlyCollection<int> patientNums) =>
        _inner.GetAdmissions(patientNums);

    public void Append(IReadOnlyList<VitalsRow> rows)
    {
        if (rows.Count > 0) Appends++;
        ThrowIf(FailAppend);
        _inner.Append(rows);
    }

    public IReadOnlyList<VitalsRow> GetVitals(int patientNum, long from, long to, int limit) => _inner.GetVitals(patientNum, from, to, limit);

    public RecordedCuff? GetLastCuff(int patientNum) => _inner.GetLastCuff(patientNum);

    public long SizeBytes => _inner.SizeBytes;

    public void Dispose() => _inner.Dispose();
}
