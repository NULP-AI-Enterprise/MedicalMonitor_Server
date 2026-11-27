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

publishes the server and a copy of the station UI into `C:\MedicalMonitor` and makes it start by itself. Run it again after pulling changes to update the installed copy; settings, access keys, patient records, the recorded history and logs are kept. The first build on a computer downloads the SQLite package from nuget.org. `Get-Help deploy\Install-Server.ps1 -Full` lists the options (`-InstallDir`, `-Urls`, `-SelfContained`, `-Uninstall`).

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

A bed is on the roster while its monitor sends data, under whatever the monitor calls its patient: the id entered at the bedside, or an "Unidentified patient" entry when it gives none. Such a patient is shown and **not recorded**. They follow the monitor: when it stops naming them - discharged at the bedside, or replaced by the next admission - they leave the roster.

A station makes them a patient of its own by admitting them:

```
POST   /api/v1/patients/{patientId}/admit    { drAlexId, patientName, bedNumber, patientType, diagnosis }
POST   /api/v1/patients/{patientId}/admit    { existingPatientId, bedNumber }      somebody who was here before
PUT    /api/v1/patients/{patientId}          the same fields, to change them
DELETE /api/v1/patients/{patientId}          discharge
```

- **Admit.** `patientId` is the entry that stands for the monitor's patient now. The server replaces it with a patient under an id it makes up (a UUID) and from that moment records their values once a second. `drAlexId` (what the hospital's DrAlex system calls them, typed in, neither checked nor unique) or `patientName` is required.
- **In between** the station decides who the patient is. Nothing the monitor reports about its own patient moves or renames them: an id changed at the bedside, a discharge on the monitor, a monitor switched off and on. The recording goes on. When the monitor starts naming somebody else, or says its patient was discharged, the stations get an advisory alert ("Monitor reports another patient"), because only a person can say whether the patient has changed. It stands until the discharge.
- **Discharge** ends the recording. The monitor, if it keeps sending, shows up again as somebody not admitted. So the order at the bedside is: discharge at the station first, then take the patient off the monitor. If DISCHARGE is forgotten, the next patient's values are recorded under the previous patient.
- **Admit again.** Somebody who was discharged and comes back - from the operating room, from another ward, the next day - is not entered a second time. `existingPatientId` is the id they were given at their first admission: the station offers the patients discharged last in its ADMIT dialog (`GET /api/v1/history/patients?discharged=true`), and COPY ID in the detail view of an admitted patient puts their id on the clipboard for later. They come back under that id, with the DrAlex id, name, type and diagnosis the history has for them - of the body only `bedNumber` counts - and their values are recorded into the same history, after a gap for the time they were away. `400` when nobody was ever admitted under the id, `409` when that patient is admitted now (one person lies in one bed: discharge them there first). Admitted as somebody new instead, the same person gets a second id and a second history, and nothing ties the two together.
- **A restart** changes nothing: admitted patients are put back on their monitors before the monitors are listened to.

An admission, an edit or a discharge that cannot be written to the history database is refused with `503` and changes nothing.

What a station enters about a patient it has *not* admitted - bed, diagnosis, a name for one the monitor does not name - is kept in `data/patients.json` under the content root (`Roster:Path`; empty keeps nothing) and comes back after a restart as soon as the patient's monitor reports them. An "Unidentified patient" entry is not kept: after a restart somebody else may be on that monitor.

## The history of admitted patients

The values are kept in one SQLite file, `data/history.db` under the content root (`History:Path`; empty turns the history off, and then nobody can be admitted). It needs no installation and survives updates of the server. Any SQLite browser opens it.

| Table | A row is | Columns |
| --- | --- | --- |
| `patients` | a patient a station admitted, however many times | `id` (the UUID), `num` (a short number the other tables refer to), `dralex_id`, `full_name`, `bed_number`, `patient_type`, `diagnosis`, `monitor_ip`, `admitted_at` and `discharged_at` of their last stay (`discharged_at` null while recorded); `standing_nibp_*` and `standing_since` are the server's own note of the cuff reading that was on the monitor when the patient came to it |
| `admissions` | one stay of a patient, from ADMIT to DISCHARGE | `patient_num`, `admitted_at`, `discharged_at` (null while it goes on), `bed_number` and `monitor_ip` as last known during the stay |
| `vitals` | one patient in one second | `patient_num`, `ts`, `hr`, `spo2`, `pr`, `rr`, `temp`, `nibp_sys`, `nibp_dia`, `nibp_map`, `nibp_at`, `extra` |

Times are Unix seconds, UTC. `extra` holds every other value the monitor measures as a JSON object keyed by the code the API uses: `{"art.sys":118,"cvp.mean":8,"ecg.st.ii":0.05}`.

```sql
SELECT datetime(v.ts, 'unixepoch', 'localtime') AS time, v.hr, v.spo2, v.extra ->> '$."art.sys"' AS art_sys
FROM vitals v JOIN patients p ON p.num = v.patient_num
WHERE p.dralex_id = '12345'
ORDER BY v.ts;
```

A row is what a station showed in that second:

- While the monitor reports, every second has a row. A monitor that reports once a second does not do it on the second, so now and then a second passes with no report in it; that second carries what the monitor said last.
- A monitor that says nothing for more than three seconds leaves a gap: no rows. A gap in the history is a monitor that was silent - or, between two rows of `admissions`, the time a patient was discharged before they were admitted again.
- A value that is not measured is null.
- A cuff (NIBP) reading is in the second it appeared and in no other: it stands on the monitor until the next one, and repeated every second it would read as measured continuously. `nibp_at` is the monitor's own time of the measurement, when it gives one. The reading that was on the monitor when the patient was admitted - the first time, or again - is not theirs and is not recorded.

The file carries the version of its tables (`PRAGMA user_version`); a newer server brings an older file up to date when it opens it, and a server that finds a file newer than itself leaves it alone and says `"Failing"`.

The same can be read over the API, with the operator key or from the station on the server's own computer (it names every patient who was ever admitted, so it is not open to the ward network like the other reads):

```
GET /api/v1/history/patients?drAlexId=&discharged=&limit=
GET /api/v1/history/patients/{patientId}
GET /api/v1/history/patients/{patientId}/vitals?from=&to=&limit=
```

Every patient comes with their stays (`admissions`). The list is the last admitted first; with `discharged=true` it is only the patients who are discharged now, the last discharged first - the ones a station can admit again. Without `from`, the values start at the patient's first admission.

`GET /api/v1/health` says `"history": "Recording"`, `"Failing"` (the file cannot be opened or written: nothing is recorded, monitoring goes on, and the stations show a banner) or `"Off"`. A file that is not a database is put aside as `history.db.unreadable-<time>` and a new one is started.

Size: roughly 4 to 30 MB a day for each bed, depending on how much its monitor measures; the log has a line with the size at start and once a day. Nothing is deleted. To back the history up while the server runs, copy `history.db` together with `history.db-wal`.

## Starting a cuff measurement from a station

A Nihon Kohden monitor read over LS-NET can be told to start an NIBP measurement and to stop the one that is running:

```
POST /api/v1/patients/{patientId}/nibp/start
POST /api/v1/patients/{patientId}/nibp/stop
```

It is off unless turned on, because it inflates a cuff on a patient at the word of somebody who is not at the bedside. Turn it on in the settings of the computer the server is installed on, `C:\MedicalMonitor\appsettings.Production.json` (kept across updates):

```json
{ "NihonKohden": { "LsNet": { "RemoteNibp": true } } }
```

`GET /api/v1/monitors` then says `canStartNibp: true` for these monitors, and the station shows NIBP START in the detail view of their beds. Both requests are writes: they need the operator key, except from the station on the server's own computer.

`202` means the command was sent to the monitor, once. The server never repeats it. What the monitor made of it is in the vitals that follow (`nibp.measuring`, then the reading) and in the log, which has a line for every request with where it came from, one for the command, one for the monitor's acknowledgement and one for the cuff starting or stopping - or a warning when it did not. A start is refused (`409`, with the reason) while the monitor reports a measurement running, for three seconds after another start, and for a monitor that is not sending data. A uMEC and a Nihon Kohden on HL7 have no such command.

## Tests

```
dotnet test MedicalMonitorServer/MedicalMonitorServer.sln
```
