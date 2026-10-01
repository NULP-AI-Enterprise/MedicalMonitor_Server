param([int]$Limit = 10)

$pHost = "localhost"
$pPort = "5432"
$pDb   = "patientmonitoring"
$pUser = "pm_user"
$pPass = "pm_password"

$psql = "C:\Program Files\PostgreSQL\18\bin\psql.exe"

Write-Host "==================================================================" -ForegroundColor Cyan
Write-Host " DATABASE VIEWER: $pDb (PostgreSQL $pHost`:$pPort)" -ForegroundColor Cyan
Write-Host "==================================================================" -ForegroundColor Cyan

if (Test-Path $psql) {
    $env:PGPASSWORD = $pPass

    Write-Host "`n--- TABLE: Patients (Registered Patients) ---" -ForegroundColor Yellow
    $q1 = 'SELECT \"Id\", \"FullName\", \"Ward\", \"Bed\", \"IsActive\", \"CreatedAt\" FROM \"Patients\" ORDER BY \"CreatedAt\" DESC LIMIT ' + $Limit + ';'
    & $psql -U $pUser -d $pDb -h $pHost -c $q1

    Write-Host "`n--- TABLE: VitalSigns (Latest Measurements) ---" -ForegroundColor Yellow
    $q2 = 'SELECT \"Id\", \"PatientId\", \"DeviceId\", \"HeartRate\" AS \"HR\", \"SystolicBloodPressure\" AS \"SBP\", \"DiastolicBloodPressure\" AS \"DBP\", \"OxygenSaturation\" AS \"SpO2\", \"Status\", \"RecordedAt\" FROM \"VitalSigns\" ORDER BY \"Id\" DESC LIMIT ' + $Limit + ';'
    & $psql -U $pUser -d $pDb -h $pHost -c $q2
} else {
    Write-Host "`nQuerying via REST API..." -ForegroundColor Yellow
    $patients = Invoke-RestMethod -Uri "http://localhost:5225/api/patients"
    $patients | Select-Object fullName, ward, bed, @{N='Status';E={$_.latestVitals.status}}, @{N='HR';E={$_.latestVitals.heartRate}}, @{N='BP';E={"$($_.latestVitals.systolicBloodPressure)/$($_.latestVitals.diastolicBloodPressure)"}}, @{N='SpO2';E={$_.latestVitals.oxygenSaturation}} | Format-Table -AutoSize
}

Write-Host "`nConnection Details for DBeaver / pgAdmin / DataGrip:" -ForegroundColor Green
Write-Host "  Host: $pHost | Port: $pPort | DB: $pDb | User: $pUser | Password: $pPass" -ForegroundColor Green
Write-Host "==================================================================" -ForegroundColor Cyan