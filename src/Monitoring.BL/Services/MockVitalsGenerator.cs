namespace Monitoring.BL.Services;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Monitoring.Shared.DTOs;

/// <summary>
/// In-process simulator standing in for real bedside monitors.
///
/// Each bed keeps a slowly drifting baseline for every vital. On top of that a patient
/// can enter a time-limited "episode" that pushes one vital out of range and then
/// recovers. Episodes are deliberately multi-second: a per-tick coin flip would make
/// alerts raise and clear twice a second, which is noise rather than a demonstration.
/// </summary>
public sealed class MockVitalsGenerator : IVitalsGenerator
{
    private sealed class PatientState
    {
        public required PatientInfoDto Info { get; init; }

        public double BaseHeartRate { get; set; }
        public double BaseSpO2 { get; set; }
        public double BaseSystolic { get; set; }
        public double BaseDiastolic { get; set; }
        public double BaseRespRate { get; set; }
        public double BaseTemperature { get; set; }

        // Continuous trace phases, carried across updates so traces never jump.
        public double EcgPhase { get; set; }
        public double PlethPhase { get; set; }
        public double RespPhase { get; set; }

        // Active abnormal episode, if any.
        public VitalSignType? EpisodeVital { get; set; }
        public double EpisodeMagnitude { get; set; }
        public int EpisodeTicksRemaining { get; set; }
        public int EpisodeTicksTotal { get; set; }
    }

    private readonly ConcurrentDictionary<string, PatientState> _patients = new();
    private readonly List<string> _order;

    private const double EpisodeStartChancePerTick = 0.006;
    private const int EpisodeMinTicks = 16;  //  8 s
    private const int EpisodeMaxTicks = 48;  // 24 s

    public MockVitalsGenerator()
    {
        var seed = new (string Name, string? Diagnosis, string Type)[]
        {
            ("Smith, John",      "Post-op CABG",            "Adu"),
            ("Doe, Jane",        "COPD exacerbation",       "Adu"),
            ("Brown, Charlie",   "Sepsis, under review",    "Adu"),
            ("Johnson, Mary",    "Acute MI",                "Adu"),
            ("Williams, James",  "Pneumonia",               "Adu"),
            ("Jones, Patricia",  "Post-op observation",     "Adu")
        };

        _order = new List<string>(seed.Length);

        for (var i = 0; i < seed.Length; i++)
        {
            var id = $"P{i + 1:D3}";
            var (name, diagnosis, type) = seed[i];

            _order.Add(id);
            _patients[id] = new PatientState
            {
                Info = new PatientInfoDto(id, name, i + 1, diagnosis, type),
                BaseHeartRate = 62 + Random.Shared.NextDouble() * 26,
                BaseSpO2 = 95 + Random.Shared.NextDouble() * 4,
                BaseSystolic = 108 + Random.Shared.NextDouble() * 22,
                BaseDiastolic = 68 + Random.Shared.NextDouble() * 14,
                BaseRespRate = 12 + Random.Shared.NextDouble() * 7,
                BaseTemperature = 36.4 + Random.Shared.NextDouble() * 0.8,
                // Stagger the starting phases so the beds don't beat in unison.
                EcgPhase = Random.Shared.NextDouble(),
                PlethPhase = Random.Shared.NextDouble(),
                RespPhase = Random.Shared.NextDouble()
            };
        }
    }

    public IReadOnlyList<PatientInfoDto> GetPatients() =>
        _order.Select(id => _patients[id].Info).ToList();

    public IReadOnlyList<MonitorUpdateDto> GenerateUpdatesForAllPatients() =>
        _order.Select(GenerateUpdate).ToList();

    public MonitorUpdateDto GenerateUpdate(string patientId)
    {
        if (!_patients.TryGetValue(patientId, out var state))
            throw new ArgumentException($"Unknown patient '{patientId}'.", nameof(patientId));

        lock (state)
        {
            DriftBaselines(state);
            AdvanceEpisode(state);

            var vitals = ComposeVitals(state);
            var waveforms = ComposeWaveforms(state, vitals);

            return new MonitorUpdateDto(
                PatientId: state.Info.PatientId,
                PatientName: state.Info.PatientName,
                BedNumber: state.Info.BedNumber,
                Vitals: vitals,
                Waveforms: waveforms,
                Timestamp: DateTimeOffset.UtcNow
            );
        }
    }

    private static void DriftBaselines(PatientState state)
    {
        state.BaseHeartRate = Nudge(state.BaseHeartRate, 0.8, 55, 105);
        state.BaseSpO2 = Nudge(state.BaseSpO2, 0.15, 94, 100);
        state.BaseSystolic = Nudge(state.BaseSystolic, 0.8, 100, 138);
        state.BaseDiastolic = Nudge(state.BaseDiastolic, 0.5, 62, 92);
        state.BaseRespRate = Nudge(state.BaseRespRate, 0.2, 11, 22);
        state.BaseTemperature = Nudge(state.BaseTemperature, 0.03, 36.1, 37.6);
    }

    private static double Nudge(double value, double step, double min, double max) =>
        Math.Clamp(value + (Random.Shared.NextDouble() - 0.5) * 2 * step, min, max);

    private static void AdvanceEpisode(PatientState state)
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
        // Negative magnitude drives the vital low, positive drives it high.
        state.EpisodeMagnitude = Random.Shared.NextDouble() < 0.5 ? -1.0 : 1.0;
        state.EpisodeTicksTotal = Random.Shared.Next(EpisodeMinTicks, EpisodeMaxTicks + 1);
        state.EpisodeTicksRemaining = state.EpisodeTicksTotal;
    }

    /// <summary>
    /// Ramps the episode in and out with a raised-cosine envelope so the numeric slides
    /// out of range and back rather than snapping between two values.
    /// </summary>
    private static double EpisodeEnvelope(PatientState state)
    {
        if (state.EpisodeTicksRemaining <= 0 || state.EpisodeTicksTotal <= 0) return 0;

        var progress = 1.0 - (double)state.EpisodeTicksRemaining / state.EpisodeTicksTotal;
        return 0.5 * (1.0 - Math.Cos(2.0 * Math.PI * progress));
    }

    private static double EpisodeOffset(PatientState state, VitalSignType vital, double fullScale)
    {
        if (state.EpisodeVital != vital) return 0;
        return state.EpisodeMagnitude * fullScale * EpisodeEnvelope(state);
    }

    private static VitalsDto ComposeVitals(PatientState state)
    {
        var hr = state.BaseHeartRate
               + EpisodeOffset(state, VitalSignType.HeartRate, 70)
               + Jitter(1.5);

        var spo2 = state.BaseSpO2
                 // SpO2 only ever alarms downward, so an episode always desaturates.
                 - Math.Abs(EpisodeOffset(state, VitalSignType.SpO2, 12))
                 + Jitter(0.4);

        var systolic = state.BaseSystolic
                     + EpisodeOffset(state, VitalSignType.Systolic, 55)
                     + Jitter(1.5);

        var diastolic = state.BaseDiastolic
                      + EpisodeOffset(state, VitalSignType.Diastolic, 32)
                      + Jitter(1.2);

        var resp = state.BaseRespRate
                 + EpisodeOffset(state, VitalSignType.RespiratoryRate, 14)
                 + Jitter(0.6);

        var temp = state.BaseTemperature
                 + EpisodeOffset(state, VitalSignType.Temperature, 2.0)
                 + Jitter(0.05);

        hr = Math.Clamp(hr, 25, 210);
        spo2 = Math.Clamp(spo2, 70, 100);
        systolic = Math.Clamp(systolic, 55, 215);
        diastolic = Math.Clamp(diastolic, 30, 130);
        resp = Math.Clamp(resp, 4, 45);
        temp = Math.Clamp(temp, 33.5, 41.5);

        // Diastolic can never exceed systolic on a real cuff reading.
        if (diastolic > systolic - 15) diastolic = systolic - 15;

        var sys = (int)Math.Round(systolic);
        var dia = (int)Math.Round(diastolic);

        return new VitalsDto(
            HeartRate: (int)Math.Round(hr),
            SpO2: (int)Math.Round(spo2),
            Nibp: new BloodPressureDto(sys, dia, (sys + 2 * dia) / 3),
            RespiratoryRate: (int)Math.Round(resp),
            Temperature: Math.Round(temp, 1),
            // The SpO2 probe measures the same beats as the ECG, with its own small error.
            PulseRate: (int)Math.Round(Math.Clamp(hr + Jitter(1.0), 25, 210))
        );
    }

    private static WaveformSampleDto[] ComposeWaveforms(PatientState state, VitalsDto vitals)
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
}
