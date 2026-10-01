using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
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
        public DateTimeOffset? LastExternalRecordedAt { get; set; }

        public VitalSignType? EpisodeVital { get; set; }
        public double EpisodeMagnitude { get; set; }
        public int EpisodeTicksRemaining { get; set; }
        public int EpisodeTicksTotal { get; set; }
    }

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IAlertStateService _alertState;
    private readonly IHubContext<VitalsHub> _hubContext;
    private readonly ILogger<CentralStationService> _logger;

    private int _connectedStations;
    private bool _databaseSynced;
    private readonly SemaphoreSlim _syncLock = new(1, 1);

    private readonly ConcurrentDictionary<string, PatientInfoDto> _patients = new();
    private readonly ConcurrentDictionary<string, VitalsDto> _latestVitals = new();
    private readonly ConcurrentDictionary<string, PatientSimulationState> _patientStates = new();
    private readonly ConcurrentDictionary<string, Guid> _patientGuidMap = new();

    private const double EpisodeStartChancePerTick = 0.005;
    private const int EpisodeMinTicks = 16;  //  8 s
    private const int EpisodeMaxTicks = 48;  // 24 s

    public int ConnectedStationsCount => Math.Max(0, Volatile.Read(ref _connectedStations));

    public CentralStationService(
        IServiceScopeFactory scopeFactory,
        IAlertStateService alertState,
        IHubContext<VitalsHub> hubContext,
        ILogger<CentralStationService> logger)
    {
        _scopeFactory = scopeFactory;
        _alertState = alertState;
        _hubContext = hubContext;
        _logger = logger;

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
            var initialVitals = new VitalsDto(
                HeartRate: (int)Math.Round(hr),
                SpO2: (int)Math.Round(spo2),
                Nibp: new BloodPressureDto(intSys, intDia, (intSys + 2 * intDia) / 3),
                RespiratoryRate: (int)Math.Round(rr),
                Temperature: Math.Round(temp, 1),
                PulseRate: (int)Math.Round(hr)
            );
            _latestVitals[id] = initialVitals;
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
        var patientId = string.IsNullOrWhiteSpace(dto.PatientId)
            ? $"P{_patients.Count + 1:D3}"
            : dto.PatientId;

        var bedNumber = GetNextUniqueBedNumber(dto.BedNumber);
        var patientInfo = dto with { PatientId = patientId, BedNumber = bedNumber };
        _patients[patientId] = patientInfo;

        var hr = 72.0;
        var spo2 = 98.0;
        var sys = 120.0;
        var dia = 80.0;
        var rr = 16.0;
        var temp = 36.6;

        _patientStates[patientId] = new PatientSimulationState
        {
            PatientId = patientId,
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

        var initialVitals = new VitalsDto(
            HeartRate: 72,
            SpO2: 98,
            Nibp: new BloodPressureDto(120, 80, 93),
            RespiratoryRate: 16,
            Temperature: 36.6,
            PulseRate: 72
        );
        _latestVitals[patientId] = initialVitals;

        var guid = DeterministicGuid(patientId);
        _patientGuidMap[patientId] = guid;

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var dbPatient = await db.Patients.FirstOrDefaultAsync(p => p.Id == guid, ct);
            if (dbPatient is null)
            {
                db.Patients.Add(new Patient
                {
                    Id = guid,
                    FullName = patientInfo.PatientName,
                    Bed = patientInfo.BedNumber.ToString(),
                    Ward = patientInfo.Diagnosis,
                    IsActive = true,
                    CreatedAt = DateTime.UtcNow
                });
                await db.SaveChangesAsync(ct);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to persist new patient {PatientId} to database.", patientId);
        }

        await _hubContext.Clients.All.SendAsync(HubMethods.PatientAdded, patientInfo, ct);
        _logger.LogInformation("Patient {PatientId} ({PatientName}) added to Central Station (Bed {Bed}).",
            patientId, patientInfo.PatientName, patientInfo.BedNumber);

        return patientInfo;
    }

    public async Task<PatientInfoDto?> UpdatePatientAsync(string patientId, PatientInfoDto dto, CancellationToken ct = default)
    {
        if (!_patients.ContainsKey(patientId))
        {
            await EnsureDatabaseSyncedAsync(ct);
            if (!_patients.ContainsKey(patientId)) return null;
        }

        var updated = dto with { PatientId = patientId };
        _patients[patientId] = updated;

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
                await db.SaveChangesAsync(ct);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to update patient {PatientId} in database.", patientId);
        }

        await _hubContext.Clients.All.SendAsync(HubMethods.PatientAdded, updated, ct);
        return updated;
    }

    public async Task<bool> DeletePatientAsync(string patientId, CancellationToken ct = default)
    {
        if (!_patients.TryRemove(patientId, out _))
        {
            await EnsureDatabaseSyncedAsync(ct);
            if (!_patients.TryRemove(patientId, out _)) return false;
        }

        _latestVitals.TryRemove(patientId, out _);
        _patientStates.TryRemove(patientId, out _);

        var cleared = _alertState.ClearForPatient(patientId);
        foreach (var alert in cleared)
        {
            await _hubContext.Clients.All.SendAsync(HubMethods.AlertCleared, alert, ct);
        }

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
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to deactivate patient {PatientId} in database.", patientId);
        }

        await _hubContext.Clients.All.SendAsync(HubMethods.PatientRemoved, patientId, ct);
        _logger.LogInformation("Patient {PatientId} removed from Central Station.", patientId);
        return true;
    }

    public VitalsDto? GetLatestVitals(string patientId) =>
        _latestVitals.GetValueOrDefault(patientId);

    public IReadOnlyList<AlertDto> GetActiveAlerts() =>
        _alertState.GetActiveAlerts();

    public async Task<AlertDto?> AcknowledgeAlertAsync(string alertId)
    {
        var acknowledged = _alertState.Acknowledge(alertId);
        if (acknowledged is null) return null;

        await _hubContext.Clients.All.SendAsync(HubMethods.AlertAcknowledged, acknowledged);
        _logger.LogInformation("Alert {AlertId} acknowledged.", alertId);
        return acknowledged;
    }

    public async Task ProcessTelemetryAsync(MonitorUpdateDto telemetry, CancellationToken ct = default)
    {
        _latestVitals[telemetry.PatientId] = telemetry.Vitals;

        if (!_patients.TryGetValue(telemetry.PatientId, out var existingInfo))
        {
            var bedNumber = GetNextUniqueBedNumber(telemetry.BedNumber);
            existingInfo = new PatientInfoDto(
                telemetry.PatientId,
                telemetry.PatientName,
                bedNumber,
                null,
                "Adu");
            _patients[telemetry.PatientId] = existingInfo;
            await _hubContext.Clients.All.SendAsync(HubMethods.PatientAdded, existingInfo, ct);
        }

        var state = _patientStates.GetOrAdd(telemetry.PatientId, id => new PatientSimulationState
        {
            PatientId = id,
            BaseHeartRate = telemetry.Vitals.HeartRate,
            BaseSpO2 = telemetry.Vitals.SpO2,
            BaseSystolic = telemetry.Vitals.Nibp.Systolic,
            BaseDiastolic = telemetry.Vitals.Nibp.Diastolic,
            BaseRespRate = telemetry.Vitals.RespiratoryRate,
            BaseTemperature = telemetry.Vitals.Temperature,
            EcgPhase = Random.Shared.NextDouble(),
            PlethPhase = Random.Shared.NextDouble(),
            RespPhase = Random.Shared.NextDouble()
        });

        lock (state)
        {
            state.LastExternalVitals = telemetry.Vitals;
            state.LastExternalRecordedAt = DateTimeOffset.UtcNow;
            state.BaseHeartRate = telemetry.Vitals.HeartRate;
            state.BaseSpO2 = telemetry.Vitals.SpO2;
            state.BaseSystolic = telemetry.Vitals.Nibp.Systolic;
            state.BaseDiastolic = telemetry.Vitals.Nibp.Diastolic;
            state.BaseRespRate = telemetry.Vitals.RespiratoryRate;
            state.BaseTemperature = telemetry.Vitals.Temperature;
        }

        await _hubContext.Clients.All.SendAsync(HubMethods.ReceiveVitals, telemetry, ct);

        var changes = _alertState.Reconcile(telemetry.PatientId, telemetry.Vitals);
        if (changes.HasChanges)
        {
            foreach (var raised in changes.Raised)
            {
                await _hubContext.Clients.All.SendAsync(HubMethods.AlertTriggered, raised, ct);
                _logger.LogWarning("ALERT {Severity} — {PatientId}: {Message}", raised.Severity, raised.PatientId, raised.Message);
            }

            foreach (var cleared in changes.Cleared)
            {
                await _hubContext.Clients.All.SendAsync(HubMethods.AlertCleared, cleared, ct);
                _logger.LogInformation("Alert cleared — {PatientId}: {Vital}", cleared.PatientId, cleared.VitalSign);
            }
        }
    }

    public async Task ProcessAlertAsync(AlertDto alert, CancellationToken ct = default)
    {
        await _hubContext.Clients.All.SendAsync(HubMethods.AlertTriggered, alert, ct);
    }

    public async Task ProcessVitalSignsRecordedAsync(Patient patient, VitalSign vitals, CancellationToken ct = default)
    {
        var patientId = GetOrMapPatientId(patient);

        if (!_patients.TryGetValue(patientId, out var existingInfo))
        {
            var preferred = int.TryParse(patient.Bed, out var b) ? b : 0;
            var bedNumber = GetNextUniqueBedNumber(preferred);
            existingInfo = new PatientInfoDto(
                patientId,
                patient.FullName,
                bedNumber,
                patient.Ward,
                "Adu");
            _patients[patientId] = existingInfo;
            await _hubContext.Clients.All.SendAsync(HubMethods.PatientAdded, existingInfo, ct);
        }

        var hr = vitals.HeartRate ?? 75;
        var spo2 = (int)Math.Round(vitals.OxygenSaturation ?? 98.0);
        var sys = vitals.SystolicBloodPressure ?? 120;
        var dia = vitals.DiastolicBloodPressure ?? 80;
        var map = (sys + 2 * dia) / 3;
        var rr = vitals.RespiratoryRate ?? 16;
        var temp = Math.Round(vitals.Temperature ?? 36.6, 1);
        var pulse = hr;

        var vitalsDto = new VitalsDto(
            HeartRate: hr,
            SpO2: spo2,
            Nibp: new BloodPressureDto(sys, dia, map),
            RespiratoryRate: rr,
            Temperature: temp,
            PulseRate: pulse
        );

        _latestVitals[patientId] = vitalsDto;

        var state = _patientStates.GetOrAdd(patientId, id => new PatientSimulationState
        {
            PatientId = id,
            BaseHeartRate = hr,
            BaseSpO2 = spo2,
            BaseSystolic = sys,
            BaseDiastolic = dia,
            BaseRespRate = rr,
            BaseTemperature = temp,
            EcgPhase = Random.Shared.NextDouble(),
            PlethPhase = Random.Shared.NextDouble(),
            RespPhase = Random.Shared.NextDouble()
        });

        lock (state)
        {
            state.LastExternalVitals = vitalsDto;
            state.LastExternalRecordedAt = DateTimeOffset.UtcNow;
            state.BaseHeartRate = hr;
            state.BaseSpO2 = spo2;
            state.BaseSystolic = sys;
            state.BaseDiastolic = dia;
            state.BaseRespRate = rr;
            state.BaseTemperature = temp;
        }

        var ecgSamples = new float[GeneratorSettings.EcgSamplesPerUpdate];
        var plethSamples = new float[GeneratorSettings.SlowSamplesPerUpdate];
        var respSamples = new float[GeneratorSettings.SlowSamplesPerUpdate];

        state.EcgPhase = WaveformSynthesizer.Fill(ecgSamples, WaveformType.ECG_II, state.EcgPhase, hr, WaveformSynthesizer.EcgSampleRate);
        state.PlethPhase = WaveformSynthesizer.Fill(plethSamples, WaveformType.Pleth, state.PlethPhase, pulse, WaveformSynthesizer.SlowSampleRate);
        state.RespPhase = WaveformSynthesizer.Fill(respSamples, WaveformType.Respiration, state.RespPhase, rr, WaveformSynthesizer.SlowSampleRate);

        var waveforms = new WaveformSampleDto[]
        {
            new(WaveformType.ECG_II, ecgSamples, WaveformSynthesizer.EcgSampleRate),
            new(WaveformType.Pleth, plethSamples, WaveformSynthesizer.SlowSampleRate),
            new(WaveformType.Respiration, respSamples, WaveformSynthesizer.SlowSampleRate)
        };

        var update = new MonitorUpdateDto(
            PatientId: patientId,
            PatientName: patient.FullName,
            BedNumber: existingInfo.BedNumber,
            Vitals: vitalsDto,
            Waveforms: waveforms,
            Timestamp: DateTimeOffset.UtcNow
        );

        await ProcessTelemetryAsync(update, ct);
    }

    public async Task PublishTickAsync(CancellationToken ct = default)
    {
        await EnsureDatabaseSyncedAsync(ct);

        var patientsList = _patients.Values.ToList();
        foreach (var patient in patientsList)
        {
            if (ct.IsCancellationRequested) break;

            var state = _patientStates.GetOrAdd(patient.PatientId, id =>
            {
                var existingVitals = _latestVitals.GetValueOrDefault(id);
                var hr = existingVitals?.HeartRate ?? (65 + Random.Shared.NextDouble() * 25);
                var spo2 = existingVitals?.SpO2 ?? (96 + Random.Shared.NextDouble() * 3);
                var sys = existingVitals?.Nibp.Systolic ?? (115 + Random.Shared.NextDouble() * 15);
                var dia = existingVitals?.Nibp.Diastolic ?? (75 + Random.Shared.NextDouble() * 10);
                var rr = existingVitals?.RespiratoryRate ?? (14 + Random.Shared.NextDouble() * 5);
                var temp = existingVitals?.Temperature ?? (36.4 + Random.Shared.NextDouble() * 0.6);

                return new PatientSimulationState
                {
                    PatientId = id,
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
            });

            VitalsDto vitals;
            WaveformSampleDto[] waveforms;

            lock (state)
            {
                // If real external vitals were supplied within the last 40 seconds, use them with subtle live jitter
                if (state.LastExternalVitals is not null &&
                    state.LastExternalRecordedAt.HasValue &&
                    DateTimeOffset.UtcNow - state.LastExternalRecordedAt.Value < TimeSpan.FromSeconds(40))
                {
                    var ext = state.LastExternalVitals;
                    var jitterHr = Math.Clamp(ext.HeartRate + Jitter(0.6), 25, 220);
                    var pulse = (int)Math.Round(Math.Clamp(jitterHr + Jitter(0.4), 25, 220));

                    vitals = ext with
                    {
                        HeartRate = (int)Math.Round(jitterHr),
                        PulseRate = pulse
                    };
                }
                else
                {
                    // Gentle physiological drift around baseline
                    DriftBaselines(state);
                    AdvanceEpisode(state);
                    vitals = ComposeVitals(state);
                }

                _latestVitals[patient.PatientId] = vitals;
                waveforms = ComposeWaveforms(state, vitals);
            }

            var update = new MonitorUpdateDto(
                PatientId: patient.PatientId,
                PatientName: patient.PatientName,
                BedNumber: patient.BedNumber,
                Vitals: vitals,
                Waveforms: waveforms,
                Timestamp: DateTimeOffset.UtcNow
            );

            await _hubContext.Clients.All.SendAsync(HubMethods.ReceiveVitals, update, ct);

            var changes = _alertState.Reconcile(patient.PatientId, vitals);
            if (changes.HasChanges)
            {
                foreach (var raised in changes.Raised)
                {
                    await _hubContext.Clients.All.SendAsync(HubMethods.AlertTriggered, raised, ct);
                    _logger.LogWarning("ALERT {Severity} — {PatientId}: {Message}", raised.Severity, raised.PatientId, raised.Message);
                }
                foreach (var cleared in changes.Cleared)
                {
                    await _hubContext.Clients.All.SendAsync(HubMethods.AlertCleared, cleared, ct);
                    _logger.LogInformation("Alert cleared — {PatientId}: {Vital}", cleared.PatientId, cleared.VitalSign);
                }
            }
        }
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
        if (_databaseSynced) return;

        await _syncLock.WaitAsync(ct);
        try
        {
            if (_databaseSynced) return;
            await SyncFromDatabaseAsync(ct);
            _databaseSynced = true;
        }
        finally
        {
            _syncLock.Release();
        }
    }

    private async Task SyncFromDatabaseAsync(CancellationToken ct)
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

            if (dbPatients.Count > 0)
            {
                var patientGuids = dbPatients.Select(p => p.Id).ToList();
                var vitalsList = await db.VitalSigns
                    .AsNoTracking()
                    .Where(v => patientGuids.Contains(v.PatientId))
                    .OrderBy(v => v.RecordedAt)
                    .ToListAsync(ct);

                var latestVitalsByPatient = vitalsList
                    .GroupBy(v => v.PatientId)
                    .ToDictionary(g => g.Key, g => g.Last());

                foreach (var p in dbPatients)
                {
                    var id = GetOrMapPatientId(p);
                    if (!_patients.TryGetValue(id, out var existingInfo))
                    {
                        var preferred = int.TryParse(p.Bed, out var b) ? b : 0;
                        var bedNumber = GetNextUniqueBedNumber(preferred);
                        existingInfo = new PatientInfoDto(id, p.FullName, bedNumber, p.Ward, "Adu");
                        _patients[id] = existingInfo;
                    }

                    if (latestVitalsByPatient.TryGetValue(p.Id, out var v))
                    {
                        var hr = v.HeartRate ?? 75;
                        var spo2 = (int)Math.Round(v.OxygenSaturation ?? 98.0);
                        var sys = v.SystolicBloodPressure ?? 120;
                        var dia = v.DiastolicBloodPressure ?? 80;
                        var map = ((int)sys + 2 * (int)dia) / 3;
                        var rr = v.RespiratoryRate ?? 16;
                        var temp = Math.Round(v.Temperature ?? 36.6, 1);

                        var vitalsDto = new VitalsDto(
                            HeartRate: (int)hr,
                            SpO2: (int)spo2,
                            Nibp: new BloodPressureDto((int)sys, (int)dia, map),
                            RespiratoryRate: (int)rr,
                            Temperature: temp,
                            PulseRate: (int)hr
                        );

                        _latestVitals[id] = vitalsDto;
                        _alertState.Reconcile(id, vitalsDto);

                        var state = _patientStates.GetOrAdd(id, _ => new PatientSimulationState
                        {
                            PatientId = id,
                            BaseHeartRate = hr,
                            BaseSpO2 = spo2,
                            BaseSystolic = sys,
                            BaseDiastolic = dia,
                            BaseRespRate = rr,
                            BaseTemperature = temp,
                            EcgPhase = Random.Shared.NextDouble(),
                            PlethPhase = Random.Shared.NextDouble(),
                            RespPhase = Random.Shared.NextDouble()
                        });

                        lock (state)
                        {
                            state.LastExternalVitals = vitalsDto;
                            state.LastExternalRecordedAt = v.RecordedAt;
                            state.BaseHeartRate = hr;
                            state.BaseSpO2 = spo2;
                            state.BaseSystolic = sys;
                            state.BaseDiastolic = dia;
                            state.BaseRespRate = rr;
                            state.BaseTemperature = temp;
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Database sync of patients encountered an issue.");
        }
    }

    private static Guid DeterministicGuid(string input)
    {
        if (Guid.TryParse(input, out var existing)) return existing;
        var bytes = MD5.HashData(Encoding.UTF8.GetBytes(input));
        return new Guid(bytes);
    }
}
