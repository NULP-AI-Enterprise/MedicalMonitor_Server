using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Monitoring.BL.Services;
using Monitoring.Shared;
using Monitoring.Shared.DTOs;
using PatientMonitoring.Api.Data;
using PatientMonitoring.Api.Hubs;
using PatientMonitoring.Api.Models;

namespace PatientMonitoring.Api.Services.CentralStation;

public sealed class CentralStationService : ICentralStationService
{
    private sealed class PatientSimulationState
    {
        public required string PatientId { get; init; }

        // Demo beds are simulated. A bed reported by a device shows only the device's own values.
        public bool IsSimulated { get; set; }

        public double BaseHeartRate { get; set; }
        public double BaseSpO2 { get; set; }
        public double BaseSystolic { get; set; }
        public double BaseDiastolic { get; set; }
        public double BaseRespRate { get; set; }
        public double BaseTemperature { get; set; }

        public double EcgPhase { get; set; }
        public double PlethPhase { get; set; }
        public double RespPhase { get; set; }

        public VitalsDto? LastExternalVitals { get; set; }

        // When the monitor last reported. A monitor silent for longer than the limit counts as disconnected.
        public DateTimeOffset LastDeviceReadingAt { get; set; }
        public bool IsStale { get; set; }

        // Samples the gateway sent, played out on the ticks at the contract's sample rates.
        public DeviceWaveformBuffer Waveforms { get; } = new();

        public VitalSignType? EpisodeVital { get; set; }
        public double EpisodeMagnitude { get; set; }
        public int EpisodeTicksRemaining { get; set; }
        public int EpisodeTicksTotal { get; set; }
    }

    /// <summary>
    /// Samples a gateway sent, waiting for their tick. Each tick takes one 500 ms slice, so the traces move at the real
    /// sample rate. At most two seconds are kept: a stalled feed must not turn into a trace that lags behind the clock.
    /// </summary>
    private sealed class DeviceWaveformBuffer
    {
        private static readonly int EcgCapacity = 2 * WaveformSynthesizer.EcgSampleRate;
        private static readonly int SlowCapacity = 2 * WaveformSynthesizer.SlowSampleRate;

        private readonly Queue<float> _ecg = new();
        private readonly Queue<float> _pleth = new();
        private readonly Queue<float> _respiration = new();

        // Pleth and respiration are centred on 0 (contract 4.4). Gateway values may sit above 0, so a slow offset is
        // removed; the time constant leaves breathing (about 0.2 Hz and up) and the pulse shape untouched.
        private readonly SlowOffsetRemover _plethOffset = new(WaveformSynthesizer.SlowSampleRate);
        private readonly SlowOffsetRemover _respirationOffset = new(WaveformSynthesizer.SlowSampleRate);

        public void Enqueue(WaveformSampleDto[]? waveforms)
        {
            foreach (var wave in waveforms ?? Array.Empty<WaveformSampleDto>())
            {
                var samples = wave.Samples ?? Array.Empty<float>();
                switch (wave.Type)
                {
                    case WaveformType.ECG_II:
                        Append(_ecg, samples, EcgCapacity, null);
                        break;
                    case WaveformType.Pleth:
                        Append(_pleth, samples, SlowCapacity, _plethOffset);
                        break;
                    case WaveformType.Respiration:
                        Append(_respiration, samples, SlowCapacity, _respirationOffset);
                        break;
                }
            }
        }

        /// <summary>One tick: up to 125 ECG and 25 Pleth/Respiration samples. Fewer only when the feed is behind.</summary>
        public WaveformSampleDto[] Take() =>
        [
            new WaveformSampleDto(WaveformType.ECG_II, Dequeue(_ecg, GeneratorSettings.EcgSamplesPerUpdate), WaveformSynthesizer.EcgSampleRate),
            new WaveformSampleDto(WaveformType.Pleth, Dequeue(_pleth, GeneratorSettings.SlowSamplesPerUpdate), WaveformSynthesizer.SlowSampleRate),
            new WaveformSampleDto(WaveformType.Respiration, Dequeue(_respiration, GeneratorSettings.SlowSamplesPerUpdate), WaveformSynthesizer.SlowSampleRate)
        ];

        public void Clear()
        {
            _ecg.Clear();
            _pleth.Clear();
            _respiration.Clear();
        }

        private static void Append(Queue<float> queue, float[] samples, int capacity, SlowOffsetRemover? offset)
        {
            foreach (var sample in samples) queue.Enqueue(offset is null ? sample : offset.Remove(sample));
            while (queue.Count > capacity) queue.Dequeue();
        }

        private static float[] Dequeue(Queue<float> queue, int count)
        {
            var taken = new float[Math.Min(count, queue.Count)];
            for (var i = 0; i < taken.Length; i++) taken[i] = queue.Dequeue();
            return taken;
        }
    }

    /// <summary>Removes a slow offset from a sampled signal: an exponential moving average with a 5 s time constant.</summary>
    private sealed class SlowOffsetRemover
    {
        private const double TimeConstantSeconds = 5.0;

        private readonly double _alpha;
        private double _level;
        private bool _started;

        public SlowOffsetRemover(int sampleRate) => _alpha = 1.0 / (TimeConstantSeconds * sampleRate);

        public float Remove(float sample)
        {
            if (!_started)
            {
                _level = sample;
                _started = true;
            }

            _level += (sample - _level) * _alpha;
            return (float)(sample - _level);
        }
    }

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IAlertStateService _alertState;
    private readonly IHubContext<VitalsHub> _hubContext;
    private readonly ILogger<CentralStationService> _logger;

    // Serialises every change to the roster, vitals, simulation and alert state, so a tick that is
    // already running cannot bring back a patient who was discharged in the meantime.
    private readonly object _rosterLock = new();

    // Alarms raised by monitors. They stay until a clinician acknowledges them, whatever the vitals say.
    private readonly ConcurrentDictionary<string, AlertDto> _deviceAlarms = new();

    private int _connectedStations;
    private bool _databaseSynced;
    private DateTimeOffset _nextSyncAttempt = DateTimeOffset.MinValue;
    private readonly SemaphoreSlim _syncLock = new(1, 1);

    private readonly ConcurrentDictionary<string, PatientInfoDto> _patients = new();
    private readonly ConcurrentDictionary<string, VitalsDto> _latestVitals = new();
    private readonly ConcurrentDictionary<string, PatientSimulationState> _patientStates = new();
    private readonly ConcurrentDictionary<string, Guid> _patientGuidMap = new();

    private const double EpisodeStartChancePerTick = 0.005;
    private const int EpisodeMinTicks = 16;  //  8 s
    private const int EpisodeMaxTicks = 48;  // 24 s

    // The Pleth shape of Monitoring.BL has a mean of about -0.166; the contract requires samples centred on 0.
    private const float PlethBaselineCorrection = 0.166f;

    // A device-fed bed whose monitor has been silent for this long counts as disconnected.
    private readonly TimeSpan _deviceStaleAfter;

    public int ConnectedStationsCount => Math.Max(0, Volatile.Read(ref _connectedStations));

    public CentralStationService(
        IServiceScopeFactory scopeFactory,
        IAlertStateService alertState,
        IHubContext<VitalsHub> hubContext,
        ILogger<CentralStationService> logger,
        IOptions<CentralStationOptions> options)
    {
        _scopeFactory = scopeFactory;
        _alertState = alertState;
        _hubContext = hubContext;
        _logger = logger;
        _deviceStaleAfter = TimeSpan.FromSeconds(Math.Max(1, options.Value.DeviceStaleAfterSeconds));

        SeedDefaultPatients();
    }

    private void SeedDefaultPatients()
    {
        var defaultRoster = new (string Id, string Name, int Bed, string? Diagnosis, string Type)[]
        {
            ("P001", "Smith, John",     1, "Post-op CABG",         "Adu"),
            ("P002", "Doe, Jane",       2, "COPD exacerbation",    "Adu"),
            ("P003", "Brown, Charlie",  3, "Sepsis, under review", "Adu"),
            ("P004", "Johnson, Mary",   4, "Acute MI",             "Adu"),
            ("P005", "Williams, James", 5, "Pneumonia",            "Adu"),
            ("P006", "Jones, Patricia", 6, "Post-op observation",  "Adu")
        };

        foreach (var (id, name, bed, diag, type) in defaultRoster)
        {
            var info = new PatientInfoDto(id, name, bed, diag, type);
            _patients[id] = info;
            _patientGuidMap[id] = DeterministicGuid(id);

            var hr = 65 + Random.Shared.NextDouble() * 25;
            var spo2 = 96 + Random.Shared.NextDouble() * 3;
            var sys = 115 + Random.Shared.NextDouble() * 15;
            var dia = 75 + Random.Shared.NextDouble() * 10;
            var rr = 14 + Random.Shared.NextDouble() * 5;
            var temp = 36.4 + Random.Shared.NextDouble() * 0.6;

            _patientStates[id] = new PatientSimulationState
            {
                PatientId = id,
                IsSimulated = true,
                BaseHeartRate = hr,
                BaseSpO2 = spo2,
                BaseSystolic = sys,
                BaseDiastolic = dia,
                BaseRespRate = rr,
                BaseTemperature = temp,
                EcgPhase = Random.Shared.NextDouble(),
                PlethPhase = Random.Shared.NextDouble(),
                RespPhase = Random.Shared.NextDouble()
            };

            var intSys = (int)Math.Round(sys);
            var intDia = (int)Math.Round(dia);
            _latestVitals[id] = new VitalsDto(
                HeartRate: (int)Math.Round(hr),
                SpO2: (int)Math.Round(spo2),
                Nibp: new BloodPressureDto(intSys, intDia, (intSys + 2 * intDia) / 3),
                RespiratoryRate: (int)Math.Round(rr),
                Temperature: Math.Round(temp, 1),
                PulseRate: (int)Math.Round(hr)
            );
        }
    }

    private int GetNextUniqueBedNumber(int preferredBed = 0)
    {
        var usedBeds = _patients.Values.Select(p => p.BedNumber).ToHashSet();
        if (preferredBed > 0 && !usedBeds.Contains(preferredBed))
        {
            return preferredBed;
        }

        int next = 1;
        while (usedBeds.Contains(next)) next++;
        return next;
    }

    private bool IsBedOccupiedByOther(string patientId, int bedNumber) =>
        _patients.Values.Any(p => p.PatientId != patientId && p.BedNumber == bedNumber);

    private string NextFreePatientId()
    {
        var n = _patients.Count + 1;
        while (_patients.ContainsKey($"P{n:D3}")) n++;
        return $"P{n:D3}";
    }

    public void IncrementConnectedStations() => Interlocked.Increment(ref _connectedStations);
    public void DecrementConnectedStations() => Interlocked.Decrement(ref _connectedStations);

    public async Task<IReadOnlyList<PatientInfoDto>> GetPatientsAsync(CancellationToken ct = default)
    {
        await EnsureDatabaseSyncedAsync(ct);
        return _patients.Values.OrderBy(p => p.BedNumber).ToList();
    }

    public async Task<PatientInfoDto?> GetPatientAsync(string patientId, CancellationToken ct = default)
    {
        if (_patients.TryGetValue(patientId, out var existing))
            return existing;

        await EnsureDatabaseSyncedAsync(ct);
        return _patients.GetValueOrDefault(patientId);
    }

    public async Task<PatientInfoDto> CreatePatientAsync(PatientInfoDto dto, CancellationToken ct = default)
    {
        await EnsureDatabaseSyncedAsync(ct);

        PatientInfoDto patientInfo;
        lock (_rosterLock)
        {
            var patientId = string.IsNullOrWhiteSpace(dto.PatientId) ? NextFreePatientId() : dto.PatientId;
            if (_patients.ContainsKey(patientId))
                throw new CentralStationConflictException($"Patient '{patientId}' already exists.");

            if (IsBedOccupiedByOther(patientId, dto.BedNumber))
                throw new CentralStationConflictException($"Bed {dto.BedNumber} is already occupied by another patient.");

            var patientType = string.IsNullOrWhiteSpace(dto.PatientType) ? "Adu" : dto.PatientType;
            patientInfo = dto with
            {
                PatientId = patientId,
                PatientType = patientType
            };
            _patients[patientId] = patientInfo;
            _patientGuidMap[patientId] = DeterministicGuid(patientId);

            // A new bed gets no simulated or invented vitals: numbers appear only when a device reports them.
        }

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var guid = DeterministicGuid(patientInfo.PatientId);

            var dbPatient = await db.Patients.FirstOrDefaultAsync(p => p.Id == guid, ct);
            if (dbPatient is null)
            {
                db.Patients.Add(new Patient
                {
                    Id = guid,
                    FullName = patientInfo.PatientName,
                    Bed = patientInfo.BedNumber.ToString(),
                    Ward = patientInfo.Diagnosis,
                    PatientType = patientInfo.PatientType,
                    StationPatientId = patientInfo.PatientId,
                    IsActive = true,
                    CreatedAt = DateTime.UtcNow
                });
            }
            else
            {
                // A patient discharged earlier and registered again: the stored record becomes active with the new data.
                dbPatient.FullName = patientInfo.PatientName;
                dbPatient.Bed = patientInfo.BedNumber.ToString();
                dbPatient.Ward = patientInfo.Diagnosis;
                dbPatient.PatientType = patientInfo.PatientType;
                dbPatient.StationPatientId = patientInfo.PatientId;
                dbPatient.IsActive = true;
            }

            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to persist new patient {PatientId} to database.", patientInfo.PatientId);
        }

        await _hubContext.Clients.All.SendAsync(HubMethods.PatientAdded, patientInfo, ct);
        _logger.LogInformation("Patient {PatientId} ({PatientName}) added to Central Station (Bed {Bed}).",
            patientInfo.PatientId, patientInfo.PatientName, patientInfo.BedNumber);

        return patientInfo;
    }

    public async Task<PatientInfoDto?> UpdatePatientAsync(string patientId, PatientInfoDto dto, CancellationToken ct = default)
    {
        await EnsureDatabaseSyncedAsync(ct);

        PatientInfoDto updated;
        lock (_rosterLock)
        {
            if (!_patients.ContainsKey(patientId)) return null;

            if (IsBedOccupiedByOther(patientId, dto.BedNumber))
                throw new CentralStationConflictException($"Bed {dto.BedNumber} is already occupied by another patient.");

            var patientType = string.IsNullOrWhiteSpace(dto.PatientType) ? "Adu" : dto.PatientType;
            updated = dto with { PatientId = patientId, PatientType = patientType };
            _patients[patientId] = updated;
        }

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var guid = _patientGuidMap.TryGetValue(patientId, out var g) ? g : DeterministicGuid(patientId);

            var dbPatient = await db.Patients.FirstOrDefaultAsync(p => p.Id == guid, ct);
            if (dbPatient is not null)
            {
                dbPatient.FullName = updated.PatientName;
                dbPatient.Bed = updated.BedNumber.ToString();
                dbPatient.Ward = updated.Diagnosis;
                dbPatient.PatientType = updated.PatientType;
                dbPatient.StationPatientId = patientId;
                await db.SaveChangesAsync(ct);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to update patient {PatientId} in database.", patientId);
        }

        await _hubContext.Clients.All.SendAsync(HubMethods.PatientAdded, updated, ct);
        return updated;
    }

    public async Task<bool> DeletePatientAsync(string patientId, CancellationToken ct = default)
    {
        await EnsureDatabaseSyncedAsync(ct);

        if (!await RemoveFromStationAsync(patientId, ct)) return false;

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var guid = _patientGuidMap.TryGetValue(patientId, out var g) ? g : DeterministicGuid(patientId);

            var dbPatient = await db.Patients.FirstOrDefaultAsync(p => p.Id == guid, ct);
            if (dbPatient is not null)
            {
                dbPatient.IsActive = false;
                await db.SaveChangesAsync(ct);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to deactivate patient {PatientId} in database.", patientId);
        }

        _logger.LogInformation("Patient {PatientId} removed from Central Station.", patientId);
        return true;
    }

    private async Task<bool> RemoveFromStationAsync(string patientId, CancellationToken ct)
    {
        IReadOnlyList<AlertDto> cleared;
        List<AlertDto> deviceAlarms;
        lock (_rosterLock)
        {
            if (!_patients.TryRemove(patientId, out _)) return false;

            _latestVitals.TryRemove(patientId, out _);
            _patientStates.TryRemove(patientId, out _);
            cleared = _alertState.ClearForPatient(patientId);

            deviceAlarms = _deviceAlarms.Values.Where(a => a.PatientId == patientId).ToList();
            foreach (var alarm in deviceAlarms)
            {
                _deviceAlarms.TryRemove(alarm.AlertId, out _);
            }
        }

        foreach (var alert in cleared.Concat(deviceAlarms))
        {
            await _hubContext.Clients.All.SendAsync(HubMethods.AlertCleared, alert, ct);
        }

        await _hubContext.Clients.All.SendAsync(HubMethods.PatientRemoved, patientId, ct);
        return true;
    }

    public VitalsDto? GetLatestVitals(string patientId) =>
        _latestVitals.GetValueOrDefault(patientId);

    public IReadOnlyList<AlertDto> GetActiveAlerts() =>
        _alertState.GetActiveAlerts()
            .Where(a => !_deviceAlarms.ContainsKey(a.AlertId))
            .Concat(_deviceAlarms.Values)
            .ToList();

    public async Task<AlertDto?> AcknowledgeAlertAsync(string alertId)
    {
        if (_deviceAlarms.TryRemove(alertId, out var alarm))
        {
            // A device alarm is cleared by the clinician's acknowledgement on every station.
            var acknowledged = alarm with { IsAcknowledged = true };
            await _hubContext.Clients.All.SendAsync(HubMethods.AlertAcknowledged, acknowledged);
            await _hubContext.Clients.All.SendAsync(HubMethods.AlertCleared, acknowledged);
            _logger.LogInformation("Device alarm {AlertId} acknowledged and cleared.", alertId);

            // The vitals may still breach the same limit; that calculated alert becomes visible again, as the stations see it.
            var calculated = GetActiveAlerts().FirstOrDefault(a => a.AlertId == alertId);
            if (calculated is not null)
            {
                await _hubContext.Clients.All.SendAsync(HubMethods.AlertTriggered, calculated);
            }

            return acknowledged;
        }

        // Every acknowledgement is announced to all stations, repeated ones too (as the reference VitalsHub does).
        var acknowledgedAlert = _alertState.Acknowledge(alertId);
        if (acknowledgedAlert is null) return null;

        await _hubContext.Clients.All.SendAsync(HubMethods.AlertAcknowledged, acknowledgedAlert);
        _logger.LogInformation("Alert {AlertId} acknowledged.", alertId);
        return acknowledgedAlert;
    }

    public Task ProcessTelemetryAsync(MonitorUpdateDto telemetry, CancellationToken ct = default) =>
        ProcessUpdateAsync(telemetry, patientType: null, ct);

    // The gateway does not send the patient type. A device reading about a stored patient carries the stored type.
    private async Task ProcessUpdateAsync(MonitorUpdateDto telemetry, string? patientType, CancellationToken ct)
    {
        if (telemetry.Vitals?.Nibp is null)
            throw new ArgumentException("Vitals and vitals.nibp are required.", nameof(telemetry));

        var vitals = NormalizeGatewayVitals(telemetry.Vitals);
        var now = DateTimeOffset.UtcNow;

        PatientInfoDto roster;
        PatientInfoDto? announce = null;
        AlertChangeSet changes;

        lock (_rosterLock)
        {
            if (_patients.TryGetValue(telemetry.PatientId, out var existing))
            {
                roster = existing;
                if (patientType is not null && existing.PatientType != patientType)
                {
                    roster = existing with { PatientType = patientType };
                    _patients[telemetry.PatientId] = roster;
                    announce = roster;
                }
            }
            else
            {
                roster = new PatientInfoDto(
                    telemetry.PatientId,
                    telemetry.PatientName,
                    GetNextUniqueBedNumber(telemetry.BedNumber),
                    null,
                    patientType ?? "Adu");
                _patients[telemetry.PatientId] = roster;
                announce = roster;
            }

            var state = _patientStates.GetOrAdd(telemetry.PatientId, id => new PatientSimulationState { PatientId = id });
            if (state.IsStale)
            {
                state.IsStale = false;   // the monitor is back, so its card returns to the station
                announce ??= roster;
            }

            state.IsSimulated = false;   // a device reports this bed, so the simulator stops driving it
            state.LastExternalVitals = vitals;
            state.LastDeviceReadingAt = now;
            state.Waveforms.Enqueue(telemetry.Waveforms);
            _latestVitals[telemetry.PatientId] = vitals;

            changes = ReconcileForStations(telemetry.PatientId, vitals);
        }

        if (announce is not null)
        {
            await _hubContext.Clients.All.SendAsync(HubMethods.PatientAdded, announce, ct);
        }

        // The frame of an incoming batch carries the numbers only: its samples are played out on the ticks.
        var frame = new MonitorUpdateDto(roster.PatientId, roster.PatientName, roster.BedNumber, vitals, EmptyWaveforms(), now);
        await _hubContext.Clients.All.SendAsync(HubMethods.ReceiveVitals, frame, ct);
        await BroadcastAlertChangesAsync(changes, ct);
    }

    public async Task<bool> ProcessAlertAsync(AlertDto alert, CancellationToken ct = default)
    {
        await EnsureDatabaseSyncedAsync(ct);

        AlertDto alarm;
        lock (_rosterLock)
        {
            if (!_patients.ContainsKey(alert.PatientId)) return false;

            // Same key as the evaluator uses: one alert per (patient, vital sign).
            alarm = alert with
            {
                AlertId = AlertDto.BuildId(alert.PatientId, alert.VitalSign),
                IsAcknowledged = false,
                Timestamp = alert.Timestamp == default ? DateTimeOffset.UtcNow : alert.Timestamp
            };

            if (_deviceAlarms.TryGetValue(alarm.AlertId, out var current))
            {
                if (current.Severity == alarm.Severity && current.Message == alarm.Message) return true;
                alarm = alarm with { Timestamp = current.Timestamp };
            }

            _deviceAlarms[alarm.AlertId] = alarm;
        }

        await _hubContext.Clients.All.SendAsync(HubMethods.AlertTriggered, alarm, ct);
        _logger.LogWarning("Device alarm {Severity} — {PatientId}: {Message}", alarm.Severity, alarm.PatientId, alarm.Message);
        return true;
    }

    public async Task ProcessVitalSignsRecordedAsync(Patient patient, VitalSign vitals, CancellationToken ct = default)
    {
        try
        {
            var patientId = GetOrMapPatientId(patient);

            VitalsDto? previous = null;
            lock (_rosterLock)
            {
                if (_patientStates.TryGetValue(patientId, out var state))
                    previous = state.LastExternalVitals;
            }

            // A reading may omit some values. Gaps are filled only from earlier device readings;
            // if a complete set is still missing, nothing is shown rather than invented numbers.
            var merged = MergeDeviceReading(vitals, previous);
            if (merged is null)
            {
                _logger.LogWarning("Incomplete reading for {PatientId} and no earlier device values to complete it; not shown on the station.", patientId);
                return;
            }

            var update = new MonitorUpdateDto(
                PatientId: patientId,
                PatientName: patient.FullName,
                BedNumber: int.TryParse(patient.Bed, out var bed) ? bed : 0,
                Vitals: merged,
                Waveforms: EmptyWaveforms(),   // no synthetic traces for device data
                Timestamp: DateTimeOffset.UtcNow);

            await ProcessUpdateAsync(update, patient.PatientType, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Failed to forward a recorded reading of patient {PatientId} to the Central Station.", patient.Id);
        }
    }

    public async Task SyncPatientAsync(Patient patient, CancellationToken ct = default)
    {
        try
        {
            var patientId = GetOrMapPatientId(patient);
            if (!patient.IsActive)
            {
                await RemoveFromStationAsync(patientId, ct);
                return;
            }

            PatientInfoDto info;
            lock (_rosterLock)
            {
                var preferredBed = int.TryParse(patient.Bed, out var bed) ? bed : 0;
                if (_patients.TryGetValue(patientId, out var current))
                {
                    var bedNumber = preferredBed > 0 && !IsBedOccupiedByOther(patientId, preferredBed)
                        ? preferredBed
                        : current.BedNumber;
                    info = current with
                    {
                        PatientName = patient.FullName,
                        BedNumber = bedNumber,
                        Diagnosis = patient.Ward,
                        PatientType = patient.PatientType
                    };
                }
                else
                {
                    info = new PatientInfoDto(patientId, patient.FullName, GetNextUniqueBedNumber(preferredBed), patient.Ward, patient.PatientType);
                }

                _patients[patientId] = info;
                _patientStates.TryAdd(patientId, new PatientSimulationState { PatientId = patientId });
            }

            await _hubContext.Clients.All.SendAsync(HubMethods.PatientAdded, info, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to sync patient {PatientId} with the Central Station.", patient.Id);
        }
    }

    public async Task PublishTickAsync(CancellationToken ct = default)
    {
        if (!_databaseSynced) _ = EnsureDatabaseSyncedAsync(CancellationToken.None);

        var now = DateTimeOffset.UtcNow;
        var frames = new List<(MonitorUpdateDto Update, AlertChangeSet Changes)>();
        var silentBeds = new List<(string PatientId, IReadOnlyList<AlertDto> Cleared)>();

        lock (_rosterLock)
        {
            foreach (var patient in _patients.Values.ToList())
            {
                if (!_patientStates.TryGetValue(patient.PatientId, out var state)) continue;

                VitalsDto vitals;
                WaveformSampleDto[] waveforms;

                if (state.IsSimulated)
                {
                    // Demo bed: gentle drift around its baseline, with occasional episodes.
                    DriftBaselines(state);
                    AdvanceEpisode(state);
                    vitals = ComposeVitals(state);
                    waveforms = ComposeWaveforms(state, vitals);
                }
                else if (state.LastExternalVitals is { } device)
                {
                    if (now - state.LastDeviceReadingAt > _deviceStaleAfter)
                    {
                        // The monitor has gone silent. Its last numbers must not keep arriving with a fresh timestamp.
                        if (!state.IsStale)
                        {
                            state.IsStale = true;
                            state.Waveforms.Clear();
                            silentBeds.Add((patient.PatientId, _alertState.ClearForPatient(patient.PatientId)));
                        }

                        continue;
                    }

                    // Device bed: the device's own numbers, with the samples the gateway sent for this tick.
                    vitals = device;
                    waveforms = state.Waveforms.Take();
                }
                else
                {
                    // Bed without any data yet: nothing to show.
                    continue;
                }

                _latestVitals[patient.PatientId] = vitals;
                var changes = ReconcileForStations(patient.PatientId, vitals);

                frames.Add((new MonitorUpdateDto(
                    PatientId: patient.PatientId,
                    PatientName: patient.PatientName,
                    BedNumber: patient.BedNumber,
                    Vitals: vitals,
                    Waveforms: waveforms,
                    Timestamp: DateTimeOffset.UtcNow), changes));
            }
        }

        foreach (var (patientId, cleared) in silentBeds)
        {
            foreach (var alert in cleared)
            {
                await _hubContext.Clients.All.SendAsync(HubMethods.AlertCleared, alert, ct);
            }

            await _hubContext.Clients.All.SendAsync(HubMethods.PatientRemoved, patientId, ct);
            _logger.LogWarning(
                "No reading from the monitor of patient {PatientId} for {Seconds} s; its card is removed from the station.",
                patientId, _deviceStaleAfter.TotalSeconds);
        }

        foreach (var (update, changes) in frames)
        {
            if (ct.IsCancellationRequested) break;

            await _hubContext.Clients.All.SendAsync(HubMethods.ReceiveVitals, update, ct);
            await BroadcastAlertChangesAsync(changes, ct);
        }
    }

    // Runs the evaluator for one patient and returns the transitions the stations must see. As in the reference
    // (IAlertStateService, HubMethods.AlertTriggered): a new breach or a severity change is announced; a continuing
    // breach, including a change of direction at the same severity, is refreshed silently.
    private AlertChangeSet ReconcileForStations(string patientId, VitalsDto vitals) =>
        _alertState.Reconcile(patientId, vitals);

    private async Task BroadcastAlertChangesAsync(AlertChangeSet changes, CancellationToken ct)
    {
        // A device alarm owns its key until a clinician acknowledges it; calculated transitions for that key stay hidden.
        foreach (var raised in changes.Raised.Where(a => !_deviceAlarms.ContainsKey(a.AlertId)))
        {
            await _hubContext.Clients.All.SendAsync(HubMethods.AlertTriggered, raised, ct);
            _logger.LogWarning("ALERT {Severity} — {PatientId}: {Message}", raised.Severity, raised.PatientId, raised.Message);
        }

        foreach (var cleared in changes.Cleared.Where(a => !_deviceAlarms.ContainsKey(a.AlertId)))
        {
            await _hubContext.Clients.All.SendAsync(HubMethods.AlertCleared, cleared, ct);
            _logger.LogInformation("Alert cleared — {PatientId}: {Vital}", cleared.PatientId, cleared.VitalSign);
        }
    }

    /// <summary>Gateway numbers follow the contract's formulas: MAP from the cuff values, temperature to one decimal.</summary>
    private static VitalsDto NormalizeGatewayVitals(VitalsDto vitals)
    {
        var cuff = vitals.Nibp;
        return vitals with
        {
            Nibp = new BloodPressureDto(cuff.Systolic, cuff.Diastolic, (cuff.Systolic + 2 * cuff.Diastolic) / 3),
            Temperature = Math.Round(vitals.Temperature, 1)
        };
    }

    private static WaveformSampleDto[] EmptyWaveforms() =>
    [
        new WaveformSampleDto(WaveformType.ECG_II, Array.Empty<float>(), WaveformSynthesizer.EcgSampleRate),
        new WaveformSampleDto(WaveformType.Pleth, Array.Empty<float>(), WaveformSynthesizer.SlowSampleRate),
        new WaveformSampleDto(WaveformType.Respiration, Array.Empty<float>(), WaveformSynthesizer.SlowSampleRate)
    ];

    /// <summary>Completes a device reading from earlier device values. Returns null while values are still missing.</summary>
    private static VitalsDto? MergeDeviceReading(VitalSign reading, VitalsDto? previous)
    {
        var hr = reading.HeartRate ?? previous?.HeartRate;
        var spo2 = reading.OxygenSaturation is { } o ? (int?)(int)Math.Round(o) : previous?.SpO2;
        var sys = reading.SystolicBloodPressure ?? previous?.Nibp.Systolic;
        var dia = reading.DiastolicBloodPressure ?? previous?.Nibp.Diastolic;
        var rr = reading.RespiratoryRate ?? previous?.RespiratoryRate;
        var temp = reading.Temperature is { } t ? (double?)Math.Round(t, 1) : previous?.Temperature;

        if (hr is null || spo2 is null || sys is null || dia is null || rr is null || temp is null)
            return null;

        var map = (sys.Value + 2 * dia.Value) / 3;
        return new VitalsDto(
            HeartRate: hr.Value,
            SpO2: spo2.Value,
            Nibp: new BloodPressureDto(sys.Value, dia.Value, map),
            RespiratoryRate: rr.Value,
            Temperature: temp.Value,
            PulseRate: hr.Value);   // no separate pulse-oximeter reading: heart rate is the best available
    }

    private static void DriftBaselines(PatientSimulationState state)
    {
        state.BaseHeartRate = Nudge(state.BaseHeartRate, 0.5, 55, 110);
        state.BaseSpO2 = Nudge(state.BaseSpO2, 0.1, 94, 100);
        state.BaseSystolic = Nudge(state.BaseSystolic, 0.5, 100, 140);
        state.BaseDiastolic = Nudge(state.BaseDiastolic, 0.3, 60, 92);
        state.BaseRespRate = Nudge(state.BaseRespRate, 0.15, 11, 22);
        state.BaseTemperature = Nudge(state.BaseTemperature, 0.02, 36.1, 37.5);
    }

    private static double Nudge(double value, double step, double min, double max) =>
        Math.Clamp(value + (Random.Shared.NextDouble() - 0.5) * 2 * step, min, max);

    private static void AdvanceEpisode(PatientSimulationState state)
    {
        if (state.EpisodeTicksRemaining > 0)
        {
            state.EpisodeTicksRemaining--;
            if (state.EpisodeTicksRemaining == 0) state.EpisodeVital = null;
            return;
        }

        if (Random.Shared.NextDouble() >= EpisodeStartChancePerTick) return;

        var candidates = Enum.GetValues<VitalSignType>();
        state.EpisodeVital = candidates[Random.Shared.Next(candidates.Length)];
        state.EpisodeMagnitude = Random.Shared.NextDouble() < 0.5 ? -1.0 : 1.0;
        state.EpisodeTicksTotal = Random.Shared.Next(EpisodeMinTicks, EpisodeMaxTicks + 1);
        state.EpisodeTicksRemaining = state.EpisodeTicksTotal;
    }

    private static double EpisodeEnvelope(PatientSimulationState state)
    {
        if (state.EpisodeTicksRemaining <= 0 || state.EpisodeTicksTotal <= 0) return 0;
        var progress = 1.0 - (double)state.EpisodeTicksRemaining / state.EpisodeTicksTotal;
        return 0.5 * (1.0 - Math.Cos(2.0 * Math.PI * progress));
    }

    private static double EpisodeOffset(PatientSimulationState state, VitalSignType vital, double fullScale)
    {
        if (state.EpisodeVital != vital) return 0;
        return state.EpisodeMagnitude * fullScale * EpisodeEnvelope(state);
    }

    private static VitalsDto ComposeVitals(PatientSimulationState state)
    {
        var hr = state.BaseHeartRate + EpisodeOffset(state, VitalSignType.HeartRate, 60) + Jitter(1.0);
        var spo2 = state.BaseSpO2 - Math.Abs(EpisodeOffset(state, VitalSignType.SpO2, 10)) + Jitter(0.3);
        var systolic = state.BaseSystolic + EpisodeOffset(state, VitalSignType.Systolic, 45) + Jitter(1.0);
        var diastolic = state.BaseDiastolic + EpisodeOffset(state, VitalSignType.Diastolic, 25) + Jitter(0.8);
        var resp = state.BaseRespRate + EpisodeOffset(state, VitalSignType.RespiratoryRate, 10) + Jitter(0.4);
        var temp = state.BaseTemperature + EpisodeOffset(state, VitalSignType.Temperature, 1.5) + Jitter(0.04);

        hr = Math.Clamp(hr, 28, 205);
        spo2 = Math.Clamp(spo2, 75, 100);
        systolic = Math.Clamp(systolic, 60, 210);
        diastolic = Math.Clamp(diastolic, 35, 125);
        resp = Math.Clamp(resp, 6, 42);
        temp = Math.Clamp(temp, 34.0, 41.0);

        if (diastolic > systolic - 15) diastolic = systolic - 15;

        var sys = (int)Math.Round(systolic);
        var dia = (int)Math.Round(diastolic);

        return new VitalsDto(
            HeartRate: (int)Math.Round(hr),
            SpO2: (int)Math.Round(spo2),
            Nibp: new BloodPressureDto(sys, dia, (sys + 2 * dia) / 3),
            RespiratoryRate: (int)Math.Round(resp),
            Temperature: Math.Round(temp, 1),
            PulseRate: (int)Math.Round(Math.Clamp(hr + Jitter(0.8), 28, 205))
        );
    }

    private static WaveformSampleDto[] ComposeWaveforms(PatientSimulationState state, VitalsDto vitals)
    {
        var ecg = new float[GeneratorSettings.EcgSamplesPerUpdate];
        state.EcgPhase = WaveformSynthesizer.Fill(
            ecg, WaveformType.ECG_II, state.EcgPhase, vitals.HeartRate, WaveformSynthesizer.EcgSampleRate);

        var pleth = new float[GeneratorSettings.SlowSamplesPerUpdate];
        state.PlethPhase = WaveformSynthesizer.Fill(
            pleth, WaveformType.Pleth, state.PlethPhase, vitals.PulseRate, WaveformSynthesizer.SlowSampleRate);
        for (var i = 0; i < pleth.Length; i++)
        {
            pleth[i] += PlethBaselineCorrection;
        }

        var resp = new float[GeneratorSettings.SlowSamplesPerUpdate];
        state.RespPhase = WaveformSynthesizer.Fill(
            resp, WaveformType.Respiration, state.RespPhase, vitals.RespiratoryRate, WaveformSynthesizer.SlowSampleRate);

        return
        [
            new WaveformSampleDto(WaveformType.ECG_II, ecg, WaveformSynthesizer.EcgSampleRate),
            new WaveformSampleDto(WaveformType.Pleth, pleth, WaveformSynthesizer.SlowSampleRate),
            new WaveformSampleDto(WaveformType.Respiration, resp, WaveformSynthesizer.SlowSampleRate)
        ];
    }

    private static double Jitter(double amplitude) => (Random.Shared.NextDouble() - 0.5) * 2 * amplitude;

    private string GetOrMapPatientId(Patient patient)
    {
        if (!string.IsNullOrWhiteSpace(patient.StationPatientId))
        {
            _patientGuidMap[patient.StationPatientId] = patient.Id;
            return patient.StationPatientId;
        }

        foreach (var (key, guid) in _patientGuidMap)
        {
            if (guid == patient.Id) return key;
        }

        var idStr = patient.Id.ToString();
        _patientGuidMap[idStr] = patient.Id;
        return idStr;
    }

    private async Task EnsureDatabaseSyncedAsync(CancellationToken ct)
    {
        if (_databaseSynced || DateTimeOffset.UtcNow < _nextSyncAttempt) return;

        await _syncLock.WaitAsync(ct);
        try
        {
            if (_databaseSynced || DateTimeOffset.UtcNow < _nextSyncAttempt) return;

            _databaseSynced = await SyncFromDatabaseAsync(ct);
            if (!_databaseSynced) _nextSyncAttempt = DateTimeOffset.UtcNow.AddSeconds(30);
        }
        finally
        {
            _syncLock.Release();
        }
    }

    private async Task<bool> SyncFromDatabaseAsync(CancellationToken ct)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var dbPatients = await db.Patients
                .AsNoTracking()
                .Where(p => p.IsActive)
                .OrderBy(p => p.CreatedAt)
                .ToListAsync(ct);

            var patientGuids = dbPatients.Select(p => p.Id).ToList();
            var latestByPatient = new Dictionary<Guid, VitalSign>();
            if (patientGuids.Count > 0)
            {
                var vitalsList = await db.VitalSigns
                    .AsNoTracking()
                    .Where(v => patientGuids.Contains(v.PatientId))
                    .OrderBy(v => v.RecordedAt)
                    .ToListAsync(ct);

                foreach (var reading in vitalsList)
                {
                    latestByPatient[reading.PatientId] = reading;   // oldest first, so the newest wins
                }
            }

            lock (_rosterLock)
            {
                foreach (var p in dbPatients)
                {
                    var id = GetOrMapPatientId(p);
                    if (!_patients.TryGetValue(id, out var roster))
                    {
                        var preferred = int.TryParse(p.Bed, out var bed) ? bed : 0;
                        _patients[id] = new PatientInfoDto(id, p.FullName, GetNextUniqueBedNumber(preferred), p.Ward, p.PatientType);
                    }
                    else if (roster.PatientType != p.PatientType)
                    {
                        _patients[id] = roster with { PatientType = p.PatientType };   // the stored type is authoritative
                    }

                    var state = _patientStates.GetOrAdd(id, _ => new PatientSimulationState { PatientId = id });

                    // Only a complete latest reading is restored; gaps are never filled with invented values.
                    if (latestByPatient.TryGetValue(p.Id, out var latest) && MergeDeviceReading(latest, null) is { } vitals)
                    {
                        state.IsSimulated = false;
                        state.LastExternalVitals = vitals;
                        state.LastDeviceReadingAt = new DateTimeOffset(DateTime.SpecifyKind(latest.RecordedAt, DateTimeKind.Utc));
                        _latestVitals[id] = vitals;
                        _alertState.Reconcile(id, vitals);
                    }
                }
            }

            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Database sync of patients failed; it will be retried.");
            return false;
        }
    }

    private static Guid DeterministicGuid(string input)
    {
        if (Guid.TryParse(input, out var existing)) return existing;
        var bytes = MD5.HashData(Encoding.UTF8.GetBytes(input));
        return new Guid(bytes);
    }
}
