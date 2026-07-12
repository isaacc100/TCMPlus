namespace TCMPlus.Infrastructure.Persistence;

public sealed class DatabaseInitializer(SqliteConnectionFactory connectionFactory)
{
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = connectionFactory.OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS stations (
                id TEXT PRIMARY KEY NOT NULL,
                name TEXT NOT NULL,
                station_type TEXT NOT NULL,
                grid_x REAL NOT NULL,
                grid_y REAL NOT NULL,
                grid_width REAL NOT NULL,
                grid_height REAL NOT NULL
            );

            CREATE TABLE IF NOT EXISTS patients (
                uid TEXT PRIMARY KEY NOT NULL,
                patient_number INTEGER NOT NULL DEFAULT 0,
                added_at_utc TEXT NOT NULL,
                current_station_id TEXT NULL,
                presenting_complaint TEXT NULL,
                discharged_at_utc TEXT NULL,
                discharge_route TEXT NULL,
                FOREIGN KEY(current_station_id) REFERENCES stations(id) ON DELETE RESTRICT
            );

            CREATE INDEX IF NOT EXISTS ix_patients_current_station_id
                ON patients(current_station_id);

            CREATE TABLE IF NOT EXISTS session_settings (
                key TEXT PRIMARY KEY NOT NULL,
                value TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS patient_events (
                id TEXT PRIMARY KEY NOT NULL,
                patient_uid TEXT NOT NULL,
                patient_number INTEGER NOT NULL,
                event_type TEXT NOT NULL,
                occurred_at_utc TEXT NOT NULL,
                from_station_name TEXT NULL,
                to_station_name TEXT NULL
            );

            CREATE INDEX IF NOT EXISTS ix_patient_events_occurred_at
                ON patient_events(occurred_at_utc);
            """;

        await command.ExecuteNonQueryAsync(cancellationToken);
        await EnsurePatientColumnAsync(connection, "patient_number", "INTEGER NOT NULL DEFAULT 0", cancellationToken);
        await EnsurePatientColumnAsync(connection, "presenting_complaint", "TEXT NULL", cancellationToken);
        await EnsurePatientColumnAsync(connection, "discharged_at_utc", "TEXT NULL", cancellationToken);
        await EnsurePatientColumnAsync(connection, "discharge_route", "TEXT NULL", cancellationToken);

        await using var backfill = connection.CreateCommand();
        backfill.CommandText = """
            UPDATE patients
            SET patient_number = (
                SELECT COUNT(*)
                FROM patients AS ordered
                WHERE ordered.added_at_utc < patients.added_at_utc
                   OR (ordered.added_at_utc = patients.added_at_utc AND ordered.uid <= patients.uid)
            )
            WHERE patient_number = 0;
            """;
        await backfill.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task EnsurePatientColumnAsync(Microsoft.Data.Sqlite.SqliteConnection connection, string name, string definition, CancellationToken cancellationToken)
    {
        await using var check = connection.CreateCommand();
        check.CommandText = "PRAGMA table_info(patients);";
        await using var reader = await check.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (string.Equals(reader.GetString(1), name, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
        }

        await using var alter = connection.CreateCommand();
        alter.CommandText = $"ALTER TABLE patients ADD COLUMN {name} {definition};";
        await alter.ExecuteNonQueryAsync(cancellationToken);
    }
}
