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
                added_at_utc TEXT NOT NULL,
                current_station_id TEXT NULL,
                FOREIGN KEY(current_station_id) REFERENCES stations(id) ON DELETE RESTRICT
            );

            CREATE INDEX IF NOT EXISTS ix_patients_current_station_id
                ON patients(current_station_id);

            CREATE TABLE IF NOT EXISTS session_settings (
                key TEXT PRIMARY KEY NOT NULL,
                value TEXT NOT NULL
            );
            """;

        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
