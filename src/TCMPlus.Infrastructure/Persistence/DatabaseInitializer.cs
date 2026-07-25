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
                grid_height REAL NOT NULL,
                sort_order INTEGER NOT NULL DEFAULT 0,
                deleted_at_utc TEXT NULL
            );

            CREATE TABLE IF NOT EXISTS mobile_teams (
                id TEXT PRIMARY KEY NOT NULL,
                callsign TEXT NOT NULL,
                note TEXT NULL,
                is_deployed INTEGER NOT NULL DEFAULT 0,
                deployment_location TEXT NULL,
                deleted_at_utc TEXT NULL
            );

            CREATE UNIQUE INDEX IF NOT EXISTS ux_mobile_teams_active_callsign
                ON mobile_teams(callsign COLLATE NOCASE)
                WHERE deleted_at_utc IS NULL;

            CREATE TABLE IF NOT EXISTS patients (
                uid TEXT PRIMARY KEY NOT NULL,
                patient_number INTEGER NOT NULL DEFAULT 0,
                added_at_utc TEXT NOT NULL,
                current_station_id TEXT NULL,
                presenting_complaint TEXT NULL,
                discharged_at_utc TEXT NULL,
                discharge_route TEXT NULL,
                discharge_outcome TEXT NULL,
                current_mobile_team_id TEXT NULL,
                FOREIGN KEY(current_station_id) REFERENCES stations(id) ON DELETE RESTRICT,
                FOREIGN KEY(current_mobile_team_id) REFERENCES mobile_teams(id) ON DELETE RESTRICT
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
                to_station_name TEXT NULL,
                from_location_kind TEXT NULL,
                to_location_kind TEXT NULL
            );

            CREATE INDEX IF NOT EXISTS ix_patient_events_occurred_at
                ON patient_events(occurred_at_utc);
            """;

        await command.ExecuteNonQueryAsync(cancellationToken);
        await EnsurePatientColumnAsync(connection, "patient_number", "INTEGER NOT NULL DEFAULT 0", cancellationToken);
        await EnsurePatientColumnAsync(connection, "presenting_complaint", "TEXT NULL", cancellationToken);
        await EnsurePatientColumnAsync(connection, "discharged_at_utc", "TEXT NULL", cancellationToken);
        await EnsurePatientColumnAsync(connection, "discharge_route", "TEXT NULL", cancellationToken);
        await EnsurePatientColumnAsync(connection, "discharge_outcome", "TEXT NULL", cancellationToken);
        await EnsurePatientColumnAsync(connection, "current_mobile_team_id", "TEXT NULL REFERENCES mobile_teams(id) ON DELETE RESTRICT", cancellationToken);
        await EnsureStationColumnAsync(connection, "deleted_at_utc", "TEXT NULL", cancellationToken);
        await EnsureStationColumnAsync(connection, "sort_order", "INTEGER NOT NULL DEFAULT 0", cancellationToken);
        await EnsurePatientEventColumnAsync(connection, "from_location_kind", "TEXT NULL", cancellationToken);
        await EnsurePatientEventColumnAsync(connection, "to_location_kind", "TEXT NULL", cancellationToken);

        await using var backfill = connection.CreateCommand();
        backfill.CommandText = """
            CREATE INDEX IF NOT EXISTS ix_patients_current_mobile_team_id
                ON patients(current_mobile_team_id);

            CREATE UNIQUE INDEX IF NOT EXISTS ux_patients_current_mobile_team_id
                ON patients(current_mobile_team_id)
                WHERE current_mobile_team_id IS NOT NULL;

            UPDATE patients
            SET patient_number = (
                SELECT COUNT(*)
                FROM patients AS ordered
                WHERE ordered.added_at_utc < patients.added_at_utc
                   OR (ordered.added_at_utc = patients.added_at_utc AND ordered.uid <= patients.uid)
            )
            WHERE patient_number = 0;

            UPDATE stations
            SET sort_order = (
                SELECT COUNT(*)
                FROM stations AS ordered
                WHERE ordered.deleted_at_utc IS NULL
                  AND (
                    ordered.name < stations.name
                    OR (ordered.name = stations.name AND ordered.station_type < stations.station_type)
                    OR (ordered.name = stations.name AND ordered.station_type = stations.station_type AND ordered.id <= stations.id)
                  )
            )
            WHERE sort_order = 0
              AND deleted_at_utc IS NULL;

            UPDATE patient_events
            SET from_location_kind = 'Station'
            WHERE from_station_name IS NOT NULL
              AND from_location_kind IS NULL;

            UPDATE patient_events
            SET to_location_kind = 'Station'
            WHERE to_station_name IS NOT NULL
              AND to_location_kind IS NULL;
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

    private static async Task EnsureStationColumnAsync(Microsoft.Data.Sqlite.SqliteConnection connection, string name, string definition, CancellationToken cancellationToken)
    {
        await using var check = connection.CreateCommand();
        check.CommandText = "PRAGMA table_info(stations);";
        await using var reader = await check.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (string.Equals(reader.GetString(1), name, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
        }

        await using var alter = connection.CreateCommand();
        alter.CommandText = $"ALTER TABLE stations ADD COLUMN {name} {definition};";
        await alter.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task EnsurePatientEventColumnAsync(Microsoft.Data.Sqlite.SqliteConnection connection, string name, string definition, CancellationToken cancellationToken)
    {
        await using var check = connection.CreateCommand();
        check.CommandText = "PRAGMA table_info(patient_events);";
        await using var reader = await check.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (string.Equals(reader.GetString(1), name, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
        }

        await using var alter = connection.CreateCommand();
        alter.CommandText = $"ALTER TABLE patient_events ADD COLUMN {name} {definition};";
        await alter.ExecuteNonQueryAsync(cancellationToken);
    }
}
