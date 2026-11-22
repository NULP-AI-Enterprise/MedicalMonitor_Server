<#
.SYNOPSIS
Installs MedicalMonitorServer on this computer, or updates the copy that is installed, and
makes it start by itself.

.DESCRIPTION
Publishes the server into -InstallDir together with a copy of the station UI, so the
installed server depends on neither repository. Run it again after pulling changes: it
stops the running copy, replaces the program and the UI, and starts it again. Settings,
access keys, patient records and logs of the installed copy are kept.

What is in the install folder:

  MedicalMonitorServer.exe ...   the server (replaced by every run of this script)
  station\                       the station UI (replaced by every run)
  appsettings.json               the settings from the repository (replaced by every run)
  appsettings.Production.json    the settings of this computer: access keys, UI folder,
                                 anything else that differs here. Written once, then kept.
  data\patients.json             what stations have recorded about patients
  logs\                          daily logs

How it starts:

  Run from an elevated PowerShell ("Run as administrator"), the server is installed as the
  Windows service "MedicalMonitorServer": it starts with the computer, before anybody logs
  in, and Windows restarts it if it stops.

  Run without administrator rights, it is registered as a task that starts when you log in
  and is restarted if it stops. It does not run while nobody is logged in.

.PARAMETER InstallDir
Where the server is installed.

.PARAMETER StationUi
The wwwroot folder of MedicalMonitor_Desktop. By default: the folder the repository's own
appsettings.Local.json names, or a MedicalMonitor_Desktop clone next to this repository.

.PARAMETER Urls
The address the server listens on, e.g. http://0.0.0.0:5230 to let other computers of the
ward network open the station. Kept from the previous install when left out; the default of
a first install is the one in appsettings.json (this computer only).

.PARAMETER SelfContained
Include the .NET runtime in the install folder, so that the folder also runs on a computer
that has no .NET installed. Needs internet access the first time.

.PARAMETER UserTask
Register the log-in task even when run as administrator.

.PARAMETER Uninstall
Stop the server and remove the service or the task. The install folder is left as it is.

.EXAMPLE
powershell -ExecutionPolicy Bypass -File deploy\Install-Server.ps1

.EXAMPLE
powershell -ExecutionPolicy Bypass -File deploy\Install-Server.ps1 -Urls http://0.0.0.0:5230
#>
[CmdletBinding()]
param(
    [string]$InstallDir = 'C:\MedicalMonitor',
    [string]$StationUi,
    [string]$Urls,
    [switch]$SelfContained,
    [switch]$UserTask,
    [switch]$Uninstall
)

$ErrorActionPreference = 'Stop'

$Name = 'MedicalMonitorServer'
$RunKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$Repo = Split-Path $PSScriptRoot -Parent
$ProjectDir = Join-Path $Repo 'MedicalMonitorServer\MedicalMonitorServer'
$Project = Join-Path $ProjectDir 'MedicalMonitorServer.csproj'
$InstallDir = [IO.Path]::GetFullPath($InstallDir)
$Exe = Join-Path $InstallDir 'MedicalMonitorServer.exe'
$Runner = Join-Path $InstallDir 'Run-Server.ps1'
$LocalSettings = Join-Path $InstallDir 'appsettings.Production.json'

$Elevated = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
    [Security.Principal.WindowsBuiltInRole]::Administrator)

function Write-Step([string]$Text) { Write-Host "== $Text" -ForegroundColor Cyan }

function Get-ServerService { Get-Service -Name $Name -ErrorAction SilentlyContinue }

function Get-ServerTask { Get-ScheduledTask -TaskName $Name -ErrorAction SilentlyContinue }

function Test-RunKey { $null -ne (Get-ItemProperty -Path $RunKey -Name $Name -ErrorAction SilentlyContinue) }

# The installed copy that is running, however it was started.
function Get-InstalledProcess {
    Get-Process -Name $Name -ErrorAction SilentlyContinue | Where-Object {
        try { $_.Path -and $_.Path.StartsWith($InstallDir, [StringComparison]::OrdinalIgnoreCase) } catch { $false }
    }
}

function Stop-Server {
    $service = Get-ServerService
    if ($service -and $service.Status -ne 'Stopped') {
        if (-not $Elevated) {
            throw "The server is installed as a Windows service. Run this script from an elevated PowerShell (Run as administrator) to update or remove it."
        }
        Stop-Service -Name $Name -Force
        $service.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(30))
    }

    $task = Get-ServerTask
    if ($task -and $task.State -eq 'Running') { Stop-ScheduledTask -TaskName $Name }

    # The task runs the server under a small script that restarts it (Run-Server.ps1): that
    # script goes first, or it would start the server again.
    Get-CimInstance Win32_Process -Filter "Name = 'powershell.exe'" -ErrorAction SilentlyContinue |
        Where-Object { $_.CommandLine -and $_.CommandLine.Contains($Runner) } |
        ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }

    $running = @(Get-InstalledProcess)
    if ($running.Count) {
        $running | Stop-Process -Force
        $running | ForEach-Object { $_.WaitForExit(10000) | Out-Null }
    }
}

function Remove-Autostart {
    if (Get-ServerService) {
        if (-not $Elevated) { throw "Removing the Windows service needs an elevated PowerShell (Run as administrator)." }
        & sc.exe delete $Name | Out-Null
    }
    if (Get-ServerTask) { Unregister-ScheduledTask -TaskName $Name -Confirm:$false }
    if (Test-RunKey) { Remove-ItemProperty -Path $RunKey -Name $Name }
}

# The settings the installed server will run with: appsettings.json, then this computer's.
function Get-Setting([string]$Section, [string]$Key) {
    $value = $null
    foreach ($file in @((Join-Path $InstallDir 'appsettings.json'), $LocalSettings)) {
        if (-not (Test-Path $file)) { continue }
        $json = Get-Content -Raw -Encoding UTF8 $file | ConvertFrom-Json
        $node = if ($Section) { $json.$Section } else { $json }
        if ($null -ne $node -and $null -ne $node.$Key) { $value = $node.$Key }
    }
    $value
}

function New-AccessKey {
    $bytes = New-Object byte[] 32
    [Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($bytes)
    [Convert]::ToBase64String($bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_')
}

# ---------------------------------------------------------------------------- uninstall

if ($Uninstall) {
    Write-Step 'Stopping the server'
    Stop-Server
    Remove-Autostart
    Write-Host "The server no longer starts by itself. Its folder, with settings, keys, patient records and logs, is left in $InstallDir."
    return
}

# ---------------------------------------------------------------------------- what to install

if (-not (Test-Path $Project)) { throw "Server project not found: $Project" }

if (-not $StationUi) {
    # The same place a server run from the repository takes its UI from.
    $repoLocal = Join-Path $ProjectDir 'appsettings.Local.json'
    if (Test-Path $repoLocal) {
        $configured = (Get-Content -Raw -Encoding UTF8 $repoLocal | ConvertFrom-Json).StationUi.Path
        if ($configured) { $StationUi = [IO.Path]::GetFullPath([IO.Path]::Combine($ProjectDir, $configured)) }
    }
}
if (-not $StationUi) {
    $StationUi = Join-Path (Split-Path $Repo -Parent) 'MedicalMonitor_Desktop\src\Monitoring.WebService\wwwroot'
}
if (-not (Test-Path (Join-Path $StationUi 'index.html'))) {
    throw "Station UI not found in '$StationUi'. Pass -StationUi <path to MedicalMonitor_Desktop\src\Monitoring.WebService\wwwroot>."
}

$asService = $Elevated -and -not $UserTask
if (-not $asService -and (Get-ServerService)) {
    throw "The server is installed as a Windows service. Run this script from an elevated PowerShell (Run as administrator)."
}

# ---------------------------------------------------------------------------- install

Write-Step 'Stopping the installed server, if it is running'
Stop-Server

Write-Step "Publishing the server to $InstallDir"
New-Item -ItemType Directory -Force $InstallDir | Out-Null
$publish = @('publish', $Project, '-c', 'Release', '-o', $InstallDir, '--nologo', '-v', 'q')
if ($SelfContained) { $publish += @('-r', 'win-x64', '--self-contained', 'true') }
& dotnet @publish
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed (exit code $LASTEXITCODE)." }

# Settings of a developer's machine have no place in an installed copy: the development
# access keys are in one of them.
foreach ($file in 'appsettings.Development.json', 'appsettings.Local.json') {
    Remove-Item (Join-Path $InstallDir $file) -Force -ErrorAction SilentlyContinue
}

Write-Step "Copying the station UI from $StationUi"
& robocopy.exe $StationUi (Join-Path $InstallDir 'station') /MIR /NFL /NDL /NJH /NJS /NP | Out-Null
if ($LASTEXITCODE -ge 8) { throw "Copying the station UI failed (robocopy exit code $LASTEXITCODE)." }
$global:LASTEXITCODE = 0

Write-Step 'Settings of this computer'
if (Test-Path $LocalSettings) {
    $settings = Get-Content -Raw -Encoding UTF8 $LocalSettings | ConvertFrom-Json
    Write-Host "Kept $LocalSettings (access keys unchanged)."
} else {
    # A server that has never been installed here gets keys of its own. The development keys
    # of the repository are known to everybody who can read it.
    $settings = [pscustomobject]@{
        Security = [pscustomobject]@{ IngestionKey = New-AccessKey; OperatorKey = New-AccessKey }
    }
    Write-Host "New access keys were generated; they are in $LocalSettings."
}
$settings | Add-Member -NotePropertyName StationUi -NotePropertyValue ([pscustomobject]@{ Path = 'station' }) -Force
if ($Urls) { $settings | Add-Member -NotePropertyName Urls -NotePropertyValue $Urls -Force }
[IO.File]::WriteAllText($LocalSettings, ($settings | ConvertTo-Json -Depth 8), (New-Object Text.UTF8Encoding $false))

$listen = Get-Setting '' 'Urls'
$firstUrl = ($listen -split ';')[0]
$healthUrl = ($firstUrl -replace '//(\*|\+|0\.0\.0\.0|\[::\])', '//localhost').TrimEnd('/')
$port = ([Uri]$healthUrl).Port

# Something else on the port - a server started with "dotnet run", most likely - would keep
# the installed one from starting, and the station would go on showing the other one.
$other = Get-NetTCPConnection -State Listen -LocalPort $port -ErrorAction SilentlyContinue | Select-Object -First 1
if ($other) {
    $owner = Get-Process -Id $other.OwningProcess -ErrorAction SilentlyContinue
    throw "Port $port is in use by $($owner.ProcessName) (process $($other.OwningProcess)). Stop it and run this script again; the installed server was not started."
}

if ($asService) {
    Write-Step 'Windows service'
    # A copy that used to start at log-in is replaced by the service.
    if (Get-ServerTask) { Unregister-ScheduledTask -TaskName $Name -Confirm:$false }
    if (Test-RunKey) { Remove-ItemProperty -Path $RunKey -Name $Name }

    if (-not (Get-ServerService)) {
        New-Service -Name $Name -BinaryPathName "`"$Exe`"" -DisplayName 'Medical Monitor Server' `
            -Description 'Collects bedside monitors and serves the central nurse station.' -StartupType Automatic | Out-Null
    }
    # Restarted by Windows if it stops: after 5 s, again after 5 s, then every minute.
    & sc.exe failure $Name reset= 86400 actions= restart/5000/restart/5000/restart/60000 | Out-Null

    # The monitors talk to the server (UDP from a Nihon Kohden, the station from other
    # computers); the rule is for this program only.
    if (-not (Get-NetFirewallRule -DisplayName 'Medical Monitor Server' -ErrorAction SilentlyContinue)) {
        New-NetFirewallRule -DisplayName 'Medical Monitor Server' -Direction Inbound -Program $Exe -Action Allow -Profile Any | Out-Null
    }

    Start-Service -Name $Name
    $how = 'Windows service "MedicalMonitorServer": starts with the computer, restarted by Windows if it stops.'
} else {
    Write-Step 'Start at log-in'
    # The server under a loop that starts it again if it stops, as the service manager would.
    $runnerText = @"
# Written by deploy\Install-Server.ps1. Keeps the server running while the user is logged in.
Set-Location -LiteralPath '$InstallDir'
while (`$true) {
    & '$Exe' *> `$null
    Start-Sleep -Seconds 5
}
"@
    [IO.File]::WriteAllText($Runner, $runnerText, (New-Object Text.UTF8Encoding $false))
    $arguments = "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File `"$Runner`""

    $registered = $false
    try {
        $action = New-ScheduledTaskAction -Execute 'powershell.exe' -Argument $arguments -WorkingDirectory $InstallDir
        $trigger = New-ScheduledTaskTrigger -AtLogOn -User "$env:USERDOMAIN\$env:USERNAME"
        # No time limit: the default would stop the server after three days.
        $taskSettings = New-ScheduledTaskSettingsSet -ExecutionTimeLimit ([TimeSpan]::Zero) -AllowStartIfOnBatteries `
            -DontStopIfGoingOnBatteries -StartWhenAvailable -MultipleInstances IgnoreNew `
            -RestartCount 3 -RestartInterval (New-TimeSpan -Minutes 1)
        Register-ScheduledTask -TaskName $Name -Action $action -Trigger $trigger -Settings $taskSettings `
            -Description 'Medical Monitor Server: bedside monitors and the central nurse station.' -Force | Out-Null
        if (Test-RunKey) { Remove-ItemProperty -Path $RunKey -Name $Name }
        Start-ScheduledTask -TaskName $Name
        $registered = $true
        $how = "Task `"$Name`" of $($env:USERNAME): starts when this user logs in, restarted if it stops. It does not run while nobody is logged in; run this script as administrator to install a Windows service instead."
    } catch {
        Write-Warning "The log-in task could not be registered ($($_.Exception.Message)); using the user's start-up list instead."
    }

    if (-not $registered) {
        Set-ItemProperty -Path $RunKey -Name $Name -Value "powershell.exe $arguments"
        Start-Process -FilePath 'powershell.exe' -ArgumentList $arguments -WindowStyle Hidden -WorkingDirectory $InstallDir
        $how = "Start-up list of $($env:USERNAME): starts when this user logs in, restarted if it stops. It does not run while nobody is logged in; run this script as administrator to install a Windows service instead."
    }
}

Write-Step 'Waiting for the server to answer'
$health = $null
foreach ($attempt in 1..40) {
    try {
        $health = Invoke-RestMethod -Uri "$healthUrl/api/v1/health" -TimeoutSec 3
        break
    } catch {
        # 503 is an answer too: the server is up and says no monitor has sent data yet.
        if ($_.Exception.Response) { $health = [pscustomobject]@{ status = "answering (HTTP $([int]$_.Exception.Response.StatusCode))" }; break }
        Start-Sleep -Milliseconds 750
    }
}
if (-not $health) {
    throw "The server did not answer on $healthUrl within 30 s. See the newest file in $InstallDir\logs."
}

Write-Host ''
Write-Host "Installed in  $InstallDir" -ForegroundColor Green
Write-Host "Station       $healthUrl"
Write-Host "Health        $($health.status)"
Write-Host "Starts as     $how"
Write-Host "Access keys   $LocalSettings (Security:IngestionKey for gateways, Security:OperatorKey for stations on other computers)"
