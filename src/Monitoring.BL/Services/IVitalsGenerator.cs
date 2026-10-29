namespace Monitoring.BL.Services;

using System.Collections.Generic;
using Monitoring.Shared.DTOs;

public interface IVitalsGenerator
{
    /// <summary>The current monitor roster, ordered by bed number.</summary>
    IReadOnlyList<PatientInfoDto> GetPatients();

    /// <summary>Advances the simulation one tick for a single patient.</summary>
    MonitorUpdateDto GenerateUpdate(string patientId);

    /// <summary>Advances the simulation one tick for every monitored bed.</summary>
    IReadOnlyList<MonitorUpdateDto> GenerateUpdatesForAllPatients();
}
