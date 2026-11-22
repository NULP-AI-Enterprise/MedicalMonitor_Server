# MedicalMonitor_Server

Reads bedside monitors (Mindray uMEC over HL7, Nihon Kohden over LS-NET) and serves the central nurse station: the web UI of [MedicalMonitor_Desktop](https://github.com/NULP-AI-Enterprise/MedicalMonitor_Desktop), a SignalR hub (`/hubs/vitals`) and a REST API (`/api/v1`).

## Running from the repository (development)

```
dotnet run --project MedicalMonitorServer/MedicalMonitorServer --launch-profile http
```

Then open <http://localhost:5230>. The `http` profile runs in the Development environment, with the access keys of `appsettings.Development.json`.

The station UI is served from the MedicalMonitor_Desktop repository (`StationUi:Path`). The default expects that repository cloned next to this one. Anywhere else, name its `src/Monitoring.WebService/wwwroot` once in `MedicalMonitorServer/MedicalMonitorServer/appsettings.Local.json`:

```json
{ "StationUi": { "Path": "C:/Repos/MedicalMonitor_Desktop/src/Monitoring.WebService/wwwroot" } }
```

`appsettings.Local.json` holds whatever is particular to one machine. It is read after the other appsettings files and before environment variables, and it is not in the repository.

## Installing on the station computer

```
powershell -ExecutionPolicy Bypass -File deploy\Install-Server.ps1
```

publishes the server and a copy of the station UI into `C:\MedicalMonitor` and makes it start by itself. Run it again after pulling changes to update the installed copy; settings, access keys, patient records and logs are kept. `Get-Help deploy\Install-Server.ps1 -Full` lists the options (`-InstallDir`, `-Urls`, `-SelfContained`, `-Uninstall`).

| Run as | The server becomes | Starts |
| --- | --- | --- |
| Administrator (elevated PowerShell) | Windows service `MedicalMonitorServer`, with a firewall rule for it | With the computer, before anybody logs in; restarted by Windows if it stops |
| An ordinary user | Scheduled task `MedicalMonitorServer` of that user | When that user logs in; restarted within seconds if it stops. Not while nobody is logged in |

Only one copy can run at a time (it holds the HTTP port and the monitors' links). Before `dotnet run` on a computer where the server is installed, stop the installed one: `Stop-Service MedicalMonitorServer`, or `Stop-ScheduledTask MedicalMonitorServer` followed by ending `MedicalMonitorServer.exe`; the installer does both when it updates.

### Access keys

The server does not start without `Security:IngestionKey` (for `/api/v1/ingestion/*`) and `Security:OperatorKey` (for every other write). The first install generates both and writes them to `C:\MedicalMonitor\appsettings.Production.json`; the development keys of this repository are not accepted by an installed server. A station opened on the server's own computer needs no key (`Security:TrustLocalStations`).

### Other computers on the ward network

By default the server listens on this computer only. `-Urls http://0.0.0.0:5230` (or `"Urls"` in `appsettings.Production.json`) opens it to the network; installed as a service, the firewall rule is already there.

## Patients

A patient is on the roster while a monitor reports them, and is removed when the monitor stops naming them: discharged at the bedside, or replaced by the next admission. That is the only way a patient is discharged; stations do not do it. A patient displaced by an operator's transfer stays on the roster without a monitor.

What stations record about a patient - bed, diagnosis, a name for one the monitor does not name - is kept in `data/patients.json` under the content root (`Roster:Path`; empty keeps nothing) and comes back after a restart as soon as the patient's monitor reports them. A monitor that gives no patient id gets an "Unidentified patient" entry, which is not kept: after a restart somebody else may be on that monitor.

## Tests

```
dotnet test MedicalMonitorServer/MedicalMonitorServer.sln
```
