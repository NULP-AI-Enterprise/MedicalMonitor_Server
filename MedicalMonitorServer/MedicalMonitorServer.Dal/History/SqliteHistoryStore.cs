using System.Globalization;
using Microsoft.Data.Sqlite;

namespace MedicalMonitorServer.Dal.History;

/// <summary>The database was written by a newer server than this one; it is left untouched.</summary>
public sealed class HistorySchemaException : Exception
{
    public HistorySchemaException(string message) : base(message)
    {
    }
}

/// <summary>
/// The history in one SQLite file. Three tables: <c>patients</c>, one row per person a station
/// admitted; <c>admissions</c>, one row per stay of theirs, for somebody who comes back is
/// admitted again under the id they have; and <c>vitals</c>, one row per patient per second,
/// kept in the order (patient, second) so that "this patient between these times" is one
/// sequential read with no index beside the table.
/// <para>
/// One connection does everything, behind a lock: a second's rows are a handful of inserts,
/// and a read of a day of one patient takes a few tens of milliseconds, so nothing waits long
/// enough to need more. Write-ahead logging with a full sync on every commit means a row that
/// was committed survives a power cut, and a process that is killed outright - which is how an
/// update stops the server - loses only the second it had not written yet.
/// </para>
/// </summary>
public sealed class SqliteHistoryStore : IHistoryStore
{
    /// <summary>
    /// What takes a database from one version of its tables to the next: entry 0 makes version 1
    /// out of an empty file, entry 1 makes version 2 out of version 1, and so on. A file is
    /// brought up to date when it is opened, step by step, so a change to the tables is a new
    /// entry here and never an edit of an old one.
    /// </summary>
    private static readonly string[] Migrations =
    {
        // 1: the patients a station admitted, and their values second by second.
        """
        CREATE TABLE patients (
            id            TEXT PRIMARY KEY,
            num           INTEGER NOT NULL UNIQUE,
            dralex_id     TEXT,
            full_name     TEXT,
            bed_number    INTEGER NOT NULL DEFAULT 0,
            patient_type  TEXT NOT NULL DEFAULT 'Adu',
            diagnosis     TEXT,
            monitor_ip    TEXT,
            admitted_at   INTEGER NOT NULL,
            discharged_at INTEGER
        );
        CREATE INDEX patients_dralex ON patients (dralex_id);
        CREATE TABLE vitals (
            patient_num INTEGER NOT NULL REFERENCES patients (num),
            ts          INTEGER NOT NULL,
            hr          INTEGER,
            spo2        INTEGER,
            pr          INTEGER,
            rr          INTEGER,
            temp        REAL,
            nibp_sys    INTEGER,
            nibp_dia    INTEGER,
            nibp_map    INTEGER,
            nibp_at     INTEGER,
            extra       TEXT,
            PRIMARY KEY (patient_num, ts)
        ) WITHOUT ROWID;
        """,

        // 2: the cuff reading that stood on the monitor when the patient came to it. It is not
        // theirs, and has to be known after a restart too, or it would be recorded as theirs
        // the first time the monitor shows it again.
        """
        ALTER TABLE patients ADD COLUMN standing_nibp_sys INTEGER;
        ALTER TABLE patients ADD COLUMN standing_nibp_dia INTEGER;
        ALTER TABLE patients ADD COLUMN standing_nibp_map INTEGER;
        ALTER TABLE patients ADD COLUMN standing_nibp_at  INTEGER;
        ALTER TABLE patients ADD COLUMN standing_since    INTEGER;
        """,

        // 3: a patient can be admitted again and go on in the history they have, so "admitted"
        // and "discharged" are no longer one pair of times per patient but one per stay.
        // patients keeps the last pair; every stay is here, and each patient there was begins
        // with the one they had. The monitor of a stay that had ended was not kept.
        """
        CREATE TABLE admissions (
            patient_num   INTEGER NOT NULL REFERENCES patients (num),
            admitted_at   INTEGER NOT NULL,
            discharged_at INTEGER,
            monitor_ip    TEXT,
            bed_number    INTEGER NOT NULL DEFAULT 0
        );
        CREATE INDEX admissions_patient ON admissions (patient_num, admitted_at);
        INSERT INTO admissions (patient_num, admitted_at, discharged_at, monitor_ip, bed_number)
            SELECT num, admitted_at, discharged_at, monitor_ip, bed_number FROM patients ORDER BY num;
        """,
    };

    /// <summary>Version of the tables this server works with, kept in the file's user_version.</summary>
    public static int SchemaVersion => Migrations.Length;

    private const string PatientColumns =
        "id, num, dralex_id, full_name, bed_number, patient_type, diagnosis, monitor_ip, admitted_at, discharged_at, " +
        "standing_nibp_sys, standing_nibp_dia, standing_nibp_map, standing_nibp_at, standing_since";

    // SQLite's own codes for "this file is not a database" and "the database is damaged".
    private const int NotADatabase = 26;
    private const int Corrupt = 11;

    private readonly object _gate = new();
    private readonly string _path;
    private SqliteConnection _connection;

    /// <summary>Set when the file found at the path could not be read and was put aside under this name.</summary>
    public string? PutAside { get; }

    /// <summary>
    /// Opens the database, creating it and its folder when they are not there. A file that is
    /// not a database is put aside, not overwritten, and a new one is started: the history in it
    /// may still be recoverable, and the server must be able to record.
    /// </summary>
    /// <exception cref="HistorySchemaException">The file was written by a newer server.</exception>
    public SqliteHistoryStore(string path)
    {
        _path = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);

        try
        {
            _connection = Open();
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode is NotADatabase or Corrupt)
        {
            PutAside = MoveAside();
            _connection = Open();
        }
    }

    public long SizeBytes
    {
        get
        {
            long size = 0;
            foreach (var file in new[] { _path, _path + "-wal" })
            {
                var info = new FileInfo(file);
                if (info.Exists) size += info.Length;
            }

            return size;
        }
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            ForeignKeys = true,
            // A pooled handle keeps the file open after Dispose, and locked on Windows.
            Pooling = false,
            DefaultTimeout = 5,
        }.ToString());

        try
        {
            connection.Open();
            Run(connection, "PRAGMA journal_mode = WAL;");
            Run(connection, "PRAGMA synchronous = FULL;");
            // The log is cut back to this after a checkpoint instead of staying at its largest.
            Run(connection, "PRAGMA journal_size_limit = 67108864;");

            var version = Convert.ToInt32(Scalar(connection, "PRAGMA user_version;"), CultureInfo.InvariantCulture);
            if (version > SchemaVersion)
            {
                throw new HistorySchemaException(
                    $"{_path} was written by a newer server (schema {version}, this server knows {SchemaVersion}); it is left as it is.");
            }

            if (version < SchemaVersion)
            {
                // All the steps or none: a file half way between two versions is neither.
                using var transaction = connection.BeginTransaction();
                for (var step = version; step < SchemaVersion; step++)
                {
                    Run(connection, Migrations[step], transaction);
                }

                Run(connection, $"PRAGMA user_version = {SchemaVersion};", transaction);
                transaction.Commit();
            }

            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    private string MoveAside()
    {
        var aside = $"{_path}.unreadable-{DateTime.Now:yyyyMMdd-HHmmss}";
        File.Move(_path, aside, overwrite: true);

        // The log and its index belong to the file they were written for, not to the next one.
        foreach (var suffix in new[] { "-wal", "-shm" })
        {
            if (File.Exists(_path + suffix)) File.Move(_path + suffix, aside + suffix, overwrite: true);
        }

        return aside;
    }

    public RecordedPatient Admit(NewRecordedPatient patient)
    {
        lock (_gate)
        {
            using var transaction = _connection.BeginTransaction();

            var num = Convert.ToInt32(
                Scalar(_connection, "SELECT COALESCE(MAX(num), 0) + 1 FROM patients;", transaction),
                CultureInfo.InvariantCulture);

            var admitted = patient.AdmittedAt.ToUnixTimeSeconds();

            using (var insert = Command(
                       "INSERT INTO patients (id, num, dralex_id, full_name, bed_number, patient_type, diagnosis, monitor_ip, admitted_at, " +
                       "standing_nibp_sys, standing_nibp_dia, standing_nibp_map, standing_nibp_at, standing_since) " +
                       "VALUES ($id, $num, $dralex, $name, $bed, $type, $diagnosis, $monitor, $admitted, $sys, $dia, $map, $at, $admitted);",
                       transaction))
            {
                insert.Parameters.AddWithValue("$id", patient.Id);
                insert.Parameters.AddWithValue("$num", num);
                insert.Parameters.AddWithValue("$dralex", Db(patient.DrAlexId));
                insert.Parameters.AddWithValue("$name", Db(patient.FullName));
                insert.Parameters.AddWithValue("$bed", patient.BedNumber);
                insert.Parameters.AddWithValue("$type", patient.PatientType);
                insert.Parameters.AddWithValue("$diagnosis", Db(patient.Diagnosis));
                insert.Parameters.AddWithValue("$monitor", patient.MonitorIp);
                insert.Parameters.AddWithValue("$admitted", admitted);
                insert.Parameters.AddWithValue("$sys", Db(patient.StandingCuff?.Systolic));
                insert.Parameters.AddWithValue("$dia", Db(patient.StandingCuff?.Diastolic));
                insert.Parameters.AddWithValue("$map", Db(patient.StandingCuff?.Mean));
                insert.Parameters.AddWithValue("$at", Db(patient.StandingCuff?.MeasuredAt));
                insert.ExecuteNonQuery();
            }

            BeginStay(num, admitted, patient.MonitorIp, patient.BedNumber, transaction);
            transaction.Commit();

            return new RecordedPatient(
                patient.Id, num, patient.DrAlexId, patient.FullName, patient.BedNumber, patient.PatientType,
                patient.Diagnosis, patient.MonitorIp,
                DateTimeOffset.FromUnixTimeSeconds(admitted), null,
                StandingCuff: patient.StandingCuff, StandingSince: admitted);
        }
    }

    public RecordedPatient? Readmit(string id, string monitorIp, int bedNumber, DateTimeOffset at, CuffReading? standingCuff)
    {
        lock (_gate)
        {
            using var transaction = _connection.BeginTransaction();

            var admitted = at.ToUnixTimeSeconds();

            // Only somebody discharged: a patient who is on a monitor now is not put on another
            // by being admitted a second time.
            using (var readmit = Command(
                       "UPDATE patients SET discharged_at = NULL, admitted_at = $admitted, monitor_ip = $monitor, bed_number = $bed, " +
                       "standing_nibp_sys = $sys, standing_nibp_dia = $dia, standing_nibp_map = $map, standing_nibp_at = $at, " +
                       "standing_since = $admitted WHERE id = $id AND discharged_at IS NOT NULL;",
                       transaction))
            {
                readmit.Parameters.AddWithValue("$id", id);
                readmit.Parameters.AddWithValue("$admitted", admitted);
                readmit.Parameters.AddWithValue("$monitor", monitorIp);
                readmit.Parameters.AddWithValue("$bed", bedNumber);
                readmit.Parameters.AddWithValue("$sys", Db(standingCuff?.Systolic));
                readmit.Parameters.AddWithValue("$dia", Db(standingCuff?.Diastolic));
                readmit.Parameters.AddWithValue("$map", Db(standingCuff?.Mean));
                readmit.Parameters.AddWithValue("$at", Db(standingCuff?.MeasuredAt));
                if (readmit.ExecuteNonQuery() == 0) return null;
            }

            RecordedPatient patient;
            using (var select = Command($"SELECT {PatientColumns}, NULL FROM patients WHERE id = $id;", transaction))
            {
                select.Parameters.AddWithValue("$id", id);
                patient = ReadPatients(select)[0];
            }

            BeginStay(patient.Num, admitted, monitorIp, bedNumber, transaction);
            transaction.Commit();
            return patient;
        }
    }

    private void BeginStay(int patientNum, long admittedAt, string monitorIp, int bedNumber, SqliteTransaction transaction)
    {
        using var insert = Command(
            "INSERT INTO admissions (patient_num, admitted_at, monitor_ip, bed_number) VALUES ($num, $admitted, $monitor, $bed);",
            transaction);
        insert.Parameters.AddWithValue("$num", patientNum);
        insert.Parameters.AddWithValue("$admitted", admittedAt);
        insert.Parameters.AddWithValue("$monitor", monitorIp);
        insert.Parameters.AddWithValue("$bed", bedNumber);
        insert.ExecuteNonQuery();
    }

    // The stay that goes on, of the patient with the id $id: what an admitted patient's bed and
    // monitor are changed in, and what a discharge ends.
    private const string OpenStayOf =
        "discharged_at IS NULL AND patient_num = (SELECT num FROM patients WHERE id = $id)";

    public void Update(string id, string? drAlexId, string? fullName, int bedNumber, string patientType, string? diagnosis)
    {
        lock (_gate)
        {
            using var transaction = _connection.BeginTransaction();

            using (var update = Command(
                       "UPDATE patients SET dralex_id = $dralex, full_name = $name, bed_number = $bed, patient_type = $type, diagnosis = $diagnosis " +
                       "WHERE id = $id;",
                       transaction))
            {
                update.Parameters.AddWithValue("$id", id);
                update.Parameters.AddWithValue("$dralex", Db(drAlexId));
                update.Parameters.AddWithValue("$name", Db(fullName));
                update.Parameters.AddWithValue("$bed", bedNumber);
                update.Parameters.AddWithValue("$type", patientType);
                update.Parameters.AddWithValue("$diagnosis", Db(diagnosis));
                update.ExecuteNonQuery();
            }

            // The bed is where they lie in this stay; the ones before keep theirs.
            using (var stay = Command($"UPDATE admissions SET bed_number = $bed WHERE {OpenStayOf};", transaction))
            {
                stay.Parameters.AddWithValue("$id", id);
                stay.Parameters.AddWithValue("$bed", bedNumber);
                stay.ExecuteNonQuery();
            }

            transaction.Commit();
        }
    }

    public void MoveMonitor(string id, string? monitorIp, string? displacedId, CuffReading? standingCuff, DateTimeOffset at)
    {
        lock (_gate)
        {
            using var transaction = _connection.BeginTransaction();

            if (displacedId is not null)
            {
                using var displace = Command("UPDATE patients SET monitor_ip = NULL WHERE id = $id;", transaction);
                displace.Parameters.AddWithValue("$id", displacedId);
                displace.ExecuteNonQuery();
            }

            using (var move = Command(
                       "UPDATE patients SET monitor_ip = $monitor, standing_nibp_sys = $sys, standing_nibp_dia = $dia, " +
                       "standing_nibp_map = $map, standing_nibp_at = $at, standing_since = $since WHERE id = $id;",
                       transaction))
            {
                move.Parameters.AddWithValue("$id", id);
                move.Parameters.AddWithValue("$monitor", Db(monitorIp));
                move.Parameters.AddWithValue("$sys", Db(standingCuff?.Systolic));
                move.Parameters.AddWithValue("$dia", Db(standingCuff?.Diastolic));
                move.Parameters.AddWithValue("$map", Db(standingCuff?.Mean));
                move.Parameters.AddWithValue("$at", Db(standingCuff?.MeasuredAt));
                move.Parameters.AddWithValue("$since", at.ToUnixTimeSeconds());
                move.ExecuteNonQuery();
            }

            if (monitorIp is not null)
            {
                // The stay keeps the monitor they were on last; having none for a while changes nothing in it.
                using var stay = Command($"UPDATE admissions SET monitor_ip = $monitor WHERE {OpenStayOf};", transaction);
                stay.Parameters.AddWithValue("$id", id);
                stay.Parameters.AddWithValue("$monitor", monitorIp);
                stay.ExecuteNonQuery();
            }

            transaction.Commit();
        }
    }

    public bool Discharge(string id, DateTimeOffset at)
    {
        lock (_gate)
        {
            using var transaction = _connection.BeginTransaction();

            // The stay first: it is found by the patient still being admitted.
            using (var stay = Command($"UPDATE admissions SET discharged_at = $at WHERE {OpenStayOf};", transaction))
            {
                stay.Parameters.AddWithValue("$id", id);
                stay.Parameters.AddWithValue("$at", at.ToUnixTimeSeconds());
                stay.ExecuteNonQuery();
            }

            bool discharged;
            using (var discharge = Command(
                       "UPDATE patients SET discharged_at = $at, monitor_ip = NULL WHERE id = $id AND discharged_at IS NULL;",
                       transaction))
            {
                discharge.Parameters.AddWithValue("$id", id);
                discharge.Parameters.AddWithValue("$at", at.ToUnixTimeSeconds());
                discharged = discharge.ExecuteNonQuery() > 0;
            }

            transaction.Commit();
            return discharged;
        }
    }

    public IReadOnlyList<RecordedPatient> GetActive()
    {
        lock (_gate)
        {
            using var select = Command(
                $"SELECT {PatientColumns}, NULL FROM patients WHERE discharged_at IS NULL ORDER BY admitted_at, num;");
            return ReadPatients(select);
        }
    }

    public IReadOnlyList<RecordedPatient> GetPatients(string? drAlexId, int limit)
    {
        lock (_gate)
        {
            // The newest second of a patient is the last entry of their run in the table: found
            // without reading the run.
            using var select = Command(
                $"SELECT {PatientColumns}, (SELECT MAX(ts) FROM vitals WHERE patient_num = patients.num) FROM patients " +
                (drAlexId is null ? string.Empty : "WHERE dralex_id = $dralex ") +
                "ORDER BY admitted_at DESC, num DESC LIMIT $limit;");
            if (drAlexId is not null) select.Parameters.AddWithValue("$dralex", drAlexId);
            select.Parameters.AddWithValue("$limit", limit);
            return ReadPatients(select);
        }
    }

    public IReadOnlyList<RecordedPatient> GetDischarged(string? drAlexId, int limit)
    {
        lock (_gate)
        {
            using var select = Command(
                $"SELECT {PatientColumns}, (SELECT MAX(ts) FROM vitals WHERE patient_num = patients.num) FROM patients " +
                "WHERE discharged_at IS NOT NULL " +
                (drAlexId is null ? string.Empty : "AND dralex_id = $dralex ") +
                "ORDER BY discharged_at DESC, num DESC LIMIT $limit;");
            if (drAlexId is not null) select.Parameters.AddWithValue("$dralex", drAlexId);
            select.Parameters.AddWithValue("$limit", limit);
            return ReadPatients(select);
        }
    }

    public RecordedPatient? GetPatient(string id)
    {
        lock (_gate)
        {
            using var select = Command(
                $"SELECT {PatientColumns}, (SELECT MAX(ts) FROM vitals WHERE patient_num = patients.num) FROM patients WHERE id = $id;");
            select.Parameters.AddWithValue("$id", id);
            return ReadPatients(select).FirstOrDefault();
        }
    }

    public IReadOnlyDictionary<int, IReadOnlyList<RecordedAdmission>> GetAdmissions(IReadOnlyCollection<int> patientNums)
    {
        var stays = new Dictionary<int, List<RecordedAdmission>>();
        if (patientNums.Count > 0)
        {
            lock (_gate)
            {
                // The numbers are numbers, so they go into the text as they are: one read for
                // the stays of everybody on a list, however long.
                using var select = Command(
                    "SELECT patient_num, admitted_at, discharged_at, bed_number, monitor_ip FROM admissions " +
                    $"WHERE patient_num IN ({string.Join(", ", patientNums.Select(num => num.ToString(CultureInfo.InvariantCulture)))}) " +
                    "ORDER BY patient_num, admitted_at, rowid;");

                using var reader = select.ExecuteReader();
                while (reader.Read())
                {
                    var num = reader.GetInt32(0);
                    if (!stays.TryGetValue(num, out var own)) stays[num] = own = new List<RecordedAdmission>();

                    own.Add(new RecordedAdmission(
                        DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(1)),
                        reader.IsDBNull(2) ? null : DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(2)),
                        reader.GetInt32(3),
                        reader.IsDBNull(4) ? null : reader.GetString(4)));
                }
            }
        }

        return stays.ToDictionary(stay => stay.Key, stay => (IReadOnlyList<RecordedAdmission>)stay.Value);
    }

    public void Append(IReadOnlyList<VitalsRow> rows)
    {
        if (rows.Count == 0) return;

        lock (_gate)
        {
            using var transaction = _connection.BeginTransaction();

            // OR REPLACE: should the clock be set back, the seconds lived twice keep what was
            // measured last instead of stopping the recording on a duplicate key.
            using var insert = Command(
                "INSERT OR REPLACE INTO vitals (patient_num, ts, hr, spo2, pr, rr, temp, nibp_sys, nibp_dia, nibp_map, nibp_at, extra) " +
                "VALUES ($num, $ts, $hr, $spo2, $pr, $rr, $temp, $sys, $dia, $map, $at, $extra);",
                transaction);
            var num = insert.Parameters.Add("$num", SqliteType.Integer);
            var ts = insert.Parameters.Add("$ts", SqliteType.Integer);
            var hr = insert.Parameters.Add("$hr", SqliteType.Integer);
            var spo2 = insert.Parameters.Add("$spo2", SqliteType.Integer);
            var pr = insert.Parameters.Add("$pr", SqliteType.Integer);
            var rr = insert.Parameters.Add("$rr", SqliteType.Integer);
            var temp = insert.Parameters.Add("$temp", SqliteType.Real);
            var sys = insert.Parameters.Add("$sys", SqliteType.Integer);
            var dia = insert.Parameters.Add("$dia", SqliteType.Integer);
            var map = insert.Parameters.Add("$map", SqliteType.Integer);
            var at = insert.Parameters.Add("$at", SqliteType.Integer);
            var extra = insert.Parameters.Add("$extra", SqliteType.Text);

            foreach (var row in rows)
            {
                num.Value = row.PatientNum;
                ts.Value = row.Timestamp;
                hr.Value = Db(row.HeartRate);
                spo2.Value = Db(row.SpO2);
                pr.Value = Db(row.PulseRate);
                rr.Value = Db(row.RespiratoryRate);
                temp.Value = Db(row.Temperature);
                sys.Value = Db(row.Cuff?.Systolic);
                dia.Value = Db(row.Cuff?.Diastolic);
                map.Value = Db(row.Cuff?.Mean);
                at.Value = Db(row.Cuff?.MeasuredAt);
                extra.Value = Db(row.Extra);
                insert.ExecuteNonQuery();
            }

            transaction.Commit();
        }
    }

    public IReadOnlyList<VitalsRow> GetVitals(int patientNum, long from, long to, int limit)
    {
        lock (_gate)
        {
            using var select = Command(
                "SELECT ts, hr, spo2, pr, rr, temp, nibp_sys, nibp_dia, nibp_map, nibp_at, extra FROM vitals " +
                "WHERE patient_num = $num AND ts >= $from AND ts <= $to ORDER BY ts DESC LIMIT $limit;");
            select.Parameters.AddWithValue("$num", patientNum);
            select.Parameters.AddWithValue("$from", from);
            select.Parameters.AddWithValue("$to", to);
            select.Parameters.AddWithValue("$limit", limit);

            var rows = new List<VitalsRow>();
            using var reader = select.ExecuteReader();
            while (reader.Read())
            {
                var cuff = reader.IsDBNull(6) && reader.IsDBNull(7) && reader.IsDBNull(8)
                    ? null
                    : new CuffReading(Int(reader, 6), Int(reader, 7), Int(reader, 8), Long(reader, 9));

                rows.Add(new VitalsRow(
                    patientNum,
                    reader.GetInt64(0),
                    Int(reader, 1),
                    Int(reader, 2),
                    Int(reader, 3),
                    Int(reader, 4),
                    reader.IsDBNull(5) ? null : reader.GetDouble(5),
                    cuff,
                    reader.IsDBNull(10) ? null : reader.GetString(10)));
            }

            rows.Reverse();
            return rows;
        }
    }

    public RecordedCuff? GetLastCuff(int patientNum)
    {
        lock (_gate)
        {
            using var select = Command(
                "SELECT nibp_sys, nibp_dia, nibp_map, nibp_at, ts FROM vitals " +
                "WHERE patient_num = $num AND (nibp_sys IS NOT NULL OR nibp_dia IS NOT NULL OR nibp_map IS NOT NULL) " +
                "ORDER BY ts DESC LIMIT 1;");
            select.Parameters.AddWithValue("$num", patientNum);

            using var reader = select.ExecuteReader();
            return reader.Read()
                ? new RecordedCuff(
                    new CuffReading(Int(reader, 0), Int(reader, 1), Int(reader, 2), Long(reader, 3)),
                    reader.GetInt64(4))
                : null;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _connection.Dispose();
        }
    }

    private static IReadOnlyList<RecordedPatient> ReadPatients(SqliteCommand select)
    {
        var patients = new List<RecordedPatient>();
        using var reader = select.ExecuteReader();
        while (reader.Read())
        {
            patients.Add(new RecordedPatient(
                reader.GetString(0),
                reader.GetInt32(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.GetInt32(4),
                reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.IsDBNull(7) ? null : reader.GetString(7),
                DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(8)),
                reader.IsDBNull(9) ? null : DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(9)),
                Long(reader, 15),
                reader.IsDBNull(10) && reader.IsDBNull(11) && reader.IsDBNull(12)
                    ? null
                    : new CuffReading(Int(reader, 10), Int(reader, 11), Int(reader, 12), Long(reader, 13)),
                Long(reader, 14)));
        }

        return patients;
    }

    private SqliteCommand Command(string sql, SqliteTransaction? transaction = null)
    {
        var command = _connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = transaction;
        return command;
    }

    private static void Run(SqliteConnection connection, string sql, SqliteTransaction? transaction = null)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = transaction;
        command.ExecuteNonQuery();
    }

    private static object? Scalar(SqliteConnection connection, string sql, SqliteTransaction? transaction = null)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = transaction;
        return command.ExecuteScalar();
    }

    private static object Db(object? value) => value ?? DBNull.Value;

    private static int? Int(SqliteDataReader reader, int column) => reader.IsDBNull(column) ? null : reader.GetInt32(column);

    private static long? Long(SqliteDataReader reader, int column) => reader.IsDBNull(column) ? null : reader.GetInt64(column);
}
