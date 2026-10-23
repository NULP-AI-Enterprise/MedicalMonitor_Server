using Microsoft.EntityFrameworkCore;
using PatientMonitoring.Api.Models;

namespace PatientMonitoring.Api.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<Patient> Patients => Set<Patient>();
    public DbSet<VitalSign> VitalSigns => Set<VitalSign>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Patient>(entity =>
        {
            entity.HasKey(p => p.Id);
            entity.Property(p => p.FullName).HasMaxLength(200).IsRequired();
            entity.Property(p => p.Ward).HasMaxLength(50);
            entity.Property(p => p.Bed).HasMaxLength(20);
            entity.HasIndex(p => p.IsActive);

            entity.HasMany(p => p.VitalSigns)
                .WithOne(v => v.Patient)
                .HasForeignKey(v => v.PatientId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<VitalSign>(entity =>
        {
            entity.HasKey(v => v.Id);
            entity.Property(v => v.DeviceId).HasMaxLength(100);
            entity.Property(v => v.Status).HasConversion<string>().HasMaxLength(20);

            // Основний запит: останні показники конкретного пацієнта
            entity.HasIndex(v => new { v.PatientId, v.RecordedAt }).IsDescending(false, true);
        });
    }
}
