using System.Buffers;
using System.Text;
using System.Text.Json;
using MedicalMonitorServer.Contracts.Api;
using MedicalMonitorServer.Dal.History;

namespace MedicalMonitorServer.Core.Monitoring;

/// <summary>
/// Turns the stream of readings into one row per patient per second, for the patients a
/// station admitted. It decides what is written; writing it is somebody else's job.
/// </summary>
public interface IVitalsRecorder
{
    /// <summary>Begins recording a patient.</summary>
    /// <param name="patientNum">The number their rows carry (<see cref="RecordedPatient.Num"/>).</param>
    /// <param name="lastCuff">
    /// The cuff reading that is not theirs to record: the one standing on the monitor when they
    /// were admitted, or the last one recorded for them before a restart.
    /// </param>
    void Start(string patientId, int patientNum, CuffReading? lastCuff);

    /// <summary>Ends the recording of a patient; what was not written yet is dropped.</summary>
    void Stop(string patientId);

    /// <summary>
    /// The patient is on another monitor now: what has not been written came from the old one
    /// and is dropped, and <paramref name="standingCuff"/>, the reading the new monitor already
    /// shows, is somebody else's.
    /// </summary>
    void Rebase(string patientId, CuffReading? standingCuff);

    /// <summary>Takes the patient's values as they stand after a message from their monitor.</summary>
    void Observe(string patientId, VitalReadings snapshot);

    /// <summary>
    /// The rows of the second <paramref name="now"/> is in: one for every recorded patient whose
    /// monitor has been heard from just now (see <see cref="VitalsRecorder.HoldFor"/>). Called
    /// about once a second.
    /// </summary>
    IReadOnlyList<VitalsRow> Drain(DateTimeOffset now);
}

/// <inheritdoc />
/// <remarks>
/// A monitor sends several messages a second, or one a minute; each leaves the patient's values
/// as a station shows them. The recorder keeps the latest of them per patient and hands it
/// over when the second is up. So a row is what the station showed in that second, and a value
/// that is not measured is null.
/// <para>
/// A monitor that reports once a second does not do it on the second: now and then a second
/// passes with no report in it, and the next has two. Such a second gets what the monitor
/// said last, as long as that is no older than <see cref="HoldFor"/> - it is what the station
/// showed then. A monitor that stays silent longer leaves a gap: a gap in the history is a
/// monitor that said nothing.
/// </para>
/// </remarks>
public sealed class VitalsRecorder : IVitalsRecorder
{
    /// <summary>
    /// How old a patient's values may be and still be written for a second in which nothing new
    /// came: enough for a second or two that a once-a-second monitor skips, not for a silence.
    /// </summary>
    public static readonly TimeSpan HoldFor = TimeSpan.FromSeconds(3);

    private sealed class Recording
    {
        public required int Num { get; init; }

        /// <summary>The patient's values as the last message left them; null until one came.</summary>
        public VitalReadings? Latest;

        public DateTimeOffset LatestAt;

        /// <summary><see cref="Latest"/> has gone into a row already.</summary>
        public bool Written;

        public CuffReading? LastCuff;
    }

    private readonly object _lock = new();
    private readonly Dictionary<string, Recording> _recordings = new(StringComparer.Ordinal);
    private readonly TimeProvider _clock;
    private long _lastSecond = long.MinValue;

    /// <param name="clock">Tells when a message came in; the system clock when omitted.</param>
    public VitalsRecorder(TimeProvider? clock = null) => _clock = clock ?? TimeProvider.System;

    public void Start(string patientId, int patientNum, CuffReading? lastCuff)
    {
        lock (_lock)
        {
            _recordings[patientId] = new Recording { Num = patientNum, LastCuff = lastCuff };
        }
    }

    public void Stop(string patientId)
    {
        lock (_lock)
        {
            _recordings.Remove(patientId);
        }
    }

    public void Rebase(string patientId, CuffReading? standingCuff)
    {
        lock (_lock)
        {
            if (!_recordings.TryGetValue(patientId, out var recording)) return;

            recording.Latest = null;
            recording.LastCuff = standingCuff;
        }
    }

    public void Observe(string patientId, VitalReadings snapshot)
    {
        var at = _clock.GetUtcNow();

        lock (_lock)
        {
            if (!_recordings.TryGetValue(patientId, out var recording)) return;

            recording.Latest = snapshot;
            recording.LatestAt = at;
            recording.Written = false;
        }
    }

    public IReadOnlyList<VitalsRow> Drain(DateTimeOffset now)
    {
        var second = now.ToUnixTimeSeconds();

        lock (_lock)
        {
            // Two calls within one second: the later one waits for the next, or a patient
            // would get two rows with one time. What has come in stays for then.
            if (second == _lastSecond) return Array.Empty<VitalsRow>();

            List<VitalsRow>? rows = null;
            foreach (var recording in _recordings.Values)
            {
                if (recording.Latest is not { } snapshot) continue;

                // Written once already and nothing new since: good for the second the monitor
                // skipped, and no longer.
                if (recording.Written && now - recording.LatestAt > HoldFor) continue;

                recording.Written = true;
                (rows ??= new()).Add(ToRow(recording, snapshot, second));
            }

            if (rows is null) return Array.Empty<VitalsRow>();

            _lastSecond = second;
            return rows;
        }
    }

    private static VitalsRow ToRow(Recording recording, VitalReadings snapshot, long second)
    {
        // A cuff reading stands in the snapshot until the next one. It is written once, in the
        // second it is first seen: repeated every second it would read as measured continuously.
        CuffReading? cuff = null;
        if (CuffOf(snapshot) is { } standing && IsNewReading(recording.LastCuff, standing))
        {
            recording.LastCuff = cuff = standing;
        }

        return new VitalsRow(
            recording.Num,
            second,
            snapshot.HeartRate,
            snapshot.SpO2,
            snapshot.PulseRate,
            snapshot.RespiratoryRate,
            snapshot.Temperature,
            cuff,
            ExtraOf(snapshot.Parameters));
    }

    /// <summary>The cuff reading in a patient's values; null when there is none.</summary>
    public static CuffReading? CuffOf(VitalReadings? readings) =>
        readings is null || (readings.Systolic is null && readings.Diastolic is null && readings.MeanArterialPressure is null)
            ? null
            : new CuffReading(
                readings.Systolic,
                readings.Diastolic,
                readings.MeanArterialPressure,
                readings.NibpMeasuredAt?.ToUnixTimeSeconds());

    /// <summary>
    /// A cuff measurement takes most of a minute, so the same numbers with times closer together
    /// than this are one reading whose time the monitor's driver has put right, not two.
    /// </summary>
    public static readonly TimeSpan ShortestBetweenReadings = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Whether <paramref name="current"/> is another measurement than <paramref name="last"/>.
    /// Other numbers are. The same numbers are only when both carry a time and the times are a
    /// measurement apart: a driver works out when the cuff was taken from what the monitor
    /// reports, and may move that time by a few seconds while the reading settles. The same
    /// numbers with a time missing are taken for the same reading: a monitor's stream that
    /// restarts shows the reading it had, without its time.
    /// </summary>
    public static bool IsNewReading(CuffReading? last, CuffReading current)
    {
        if (last is null) return true;

        if (last.Systolic != current.Systolic || last.Diastolic != current.Diastolic || last.Mean != current.Mean) return true;

        return last.MeasuredAt is { } before && current.MeasuredAt is { } now
            && Math.Abs(now - before) >= (long)ShortestBetweenReadings.TotalSeconds;
    }

    /// <summary>The other values as a JSON object, code to number, in the order of the codes.</summary>
    private static string? ExtraOf(IReadOnlyDictionary<string, ParameterDto>? parameters)
    {
        if (parameters is null || parameters.Count == 0) return null;

        var buffer = new ArrayBufferWriter<byte>(256);
        var written = false;
        using (var json = new Utf8JsonWriter(buffer))
        {
            json.WriteStartObject();
            foreach (var (code, parameter) in parameters.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                // JSON has no word for "not a number"; such a value is not a measurement either.
                if (!double.IsFinite(parameter.Value)) continue;

                json.WriteNumber(code, parameter.Value);
                written = true;
            }

            json.WriteEndObject();
        }

        return written ? Encoding.UTF8.GetString(buffer.WrittenSpan) : null;
    }
}
