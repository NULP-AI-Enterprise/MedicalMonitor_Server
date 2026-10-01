using Microsoft.EntityFrameworkCore;
using PatientMonitoring.Api.Contracts;
using PatientMonitoring.Api.Data;
using PatientMonitoring.Api.Models;

using PatientMonitoring.Api.Services.Caching;

namespace PatientMonitoring.Api.Services;

public interface IPatientService
{
    Task<IReadOnlyList<PatientResponse>> GetAllAsync(bool includeInactive, CancellationToken cancellationToken = default);

    Task<PatientResponse?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);

    Task<bool> ExistsAsync(Guid id, CancellationToken cancellationToken = default);

    Task<PatientResponse> CreateAsync(CreatePatientRequest request, CancellationToken cancellationToken = default);

    Task<PatientResponse?> UpdateAsync(Guid id, UpdatePatientRequest request, CancellationToken cancellationToken = default);

    /// <summary>Деактивує пацієнта (виписка). Історія показників зберігається.</summary>
    Task<bool> DeactivateAsync(Guid id, CancellationToken cancellationToken = default);
}

public sealed class PatientService(AppDbContext db, IVitalSignsAnalyzer analyzer, IVitalSignsCache cache) : IPatientService
{
    public async Task<IReadOnlyList<PatientResponse>> GetAllAsync(bool includeInactive, CancellationToken cancellationToken = default)
    {
        var query = db.Patients.AsNoTracking();

        if (!includeInactive)
        {
            query = query.Where(p => p.IsActive);
        }

        var rows = await query
            .OrderBy(p => p.FullName)
            .Select(p => new
            {
                Patient = p,
                Latest = p.VitalSigns.OrderByDescending(v => v.RecordedAt).FirstOrDefault()
            })
            .ToListAsync(cancellationToken);

        return rows
            .Select(r => r.Patient.ToResponse(r.Latest?.ToResponse(analyzer, r.Patient.FullName)))
            .ToList();
    }

    public async Task<PatientResponse?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var row = await db.Patients
            .AsNoTracking()
            .Where(p => p.Id == id)
            .Select(p => new
            {
                Patient = p,
                Latest = p.VitalSigns.OrderByDescending(v => v.RecordedAt).FirstOrDefault()
            })
            .FirstOrDefaultAsync(cancellationToken);

        return row?.Patient.ToResponse(row.Latest?.ToResponse(analyzer, row.Patient.FullName));
    }

    public Task<bool> ExistsAsync(Guid id, CancellationToken cancellationToken = default)
        => db.Patients.AnyAsync(p => p.Id == id, cancellationToken);

    public async Task<PatientResponse> CreateAsync(CreatePatientRequest request, CancellationToken cancellationToken = default)
    {
        var patient = new Patient
        {
            Id = Guid.CreateVersion7(),
            FullName = request.FullName.Trim(),
            DateOfBirth = request.DateOfBirth,
            Ward = request.Ward?.Trim(),
            Bed = request.Bed?.Trim(),
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        };

        db.Patients.Add(patient);
        await db.SaveChangesAsync(cancellationToken);

        return patient.ToResponse(latestVitals: null);
    }

    public async Task<PatientResponse?> UpdateAsync(Guid id, UpdatePatientRequest request, CancellationToken cancellationToken = default)
    {
        var patient = await db.Patients.FirstOrDefaultAsync(p => p.Id == id, cancellationToken);
        if (patient is null)
        {
            return null;
        }

        patient.FullName = request.FullName.Trim();
        patient.DateOfBirth = request.DateOfBirth;
        patient.Ward = request.Ward?.Trim();
        patient.Bed = request.Bed?.Trim();
        patient.IsActive = request.IsActive;

        await db.SaveChangesAsync(cancellationToken);
        await cache.InvalidateAsync(id, cancellationToken);

        return await GetByIdAsync(id, cancellationToken);
    }

    public async Task<bool> DeactivateAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var patient = await db.Patients.FirstOrDefaultAsync(p => p.Id == id, cancellationToken);
        if (patient is null)
        {
            return false;
        }

        patient.IsActive = false;
        await db.SaveChangesAsync(cancellationToken);
        await cache.InvalidateAsync(id, cancellationToken);
        return true;
    }
}
