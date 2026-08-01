using Microsoft.Data.Sqlite;
using TCMPlus.Domain.Models;
using TCMPlus.Domain.Persistence;

namespace TCMPlus.Infrastructure.Persistence;

public sealed class SqliteTreatmentCentreLayoutRepository(SqliteConnectionFactory connectionFactory)
    : ITreatmentCentreLayoutRepository
{
    public async Task CommitAsync(
        TreatmentCentreLayout layout,
        DateTimeOffset deletedAt,
        CancellationToken cancellationToken = default)
    {
        await using var connection = connectionFactory.OpenConnection();
        using var transaction = connection.BeginTransaction();

        var retainedIds = layout.Stations.Select(station => station.Id.ToString("N")).ToList();
        if (retainedIds.Count == 0)
        {
            await SoftDeleteMissingAsync(connection, transaction, null, deletedAt, cancellationToken);
        }
        else
        {
            await SoftDeleteMissingAsync(connection, transaction, retainedIds, deletedAt, cancellationToken);
        }

        for (var index = 0; index < layout.Stations.Count; index++)
        {
            var station = layout.Stations[index];
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO stations (id, name, station_type, grid_x, grid_y, grid_width, grid_height, sort_order, deleted_at_utc)
                VALUES (@id, @name, @type, @gridX, @gridY, @gridWidth, @gridHeight, @sortOrder, NULL)
                ON CONFLICT(id) DO UPDATE SET
                    name = excluded.name,
                    station_type = excluded.station_type,
                    grid_x = excluded.grid_x,
                    grid_y = excluded.grid_y,
                    grid_width = excluded.grid_width,
                    grid_height = excluded.grid_height,
                    sort_order = excluded.sort_order,
                    deleted_at_utc = NULL;
                """;
            command.Parameters.AddWithValue("@id", station.Id.ToString("N"));
            command.Parameters.AddWithValue("@name", station.Name.Trim());
            command.Parameters.AddWithValue("@type", station.Type.Trim());
            command.Parameters.AddWithValue("@gridX", station.GridX);
            command.Parameters.AddWithValue("@gridY", station.GridY);
            command.Parameters.AddWithValue("@gridWidth", station.GridWidth);
            command.Parameters.AddWithValue("@gridHeight", station.GridHeight);
            command.Parameters.AddWithValue("@sortOrder", index + 1);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var settingsCommand = connection.CreateCommand())
        {
            settingsCommand.Transaction = transaction;
            settingsCommand.CommandText = """
                INSERT INTO session_settings (key, value) VALUES ('grid_density', @value)
                ON CONFLICT(key) DO UPDATE SET value = excluded.value;
                """;
            settingsCommand.Parameters.AddWithValue("@value", layout.GridDensity.ToString());
            await settingsCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    private static async Task SoftDeleteMissingAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IReadOnlyList<string>? retainedIds,
        DateTimeOffset deletedAt,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.Parameters.AddWithValue("@deletedAt", deletedAt.UtcDateTime.ToString("O"));
        if (retainedIds is null)
        {
            command.CommandText = "UPDATE stations SET deleted_at_utc = @deletedAt WHERE deleted_at_utc IS NULL;";
        }
        else
        {
            var names = new List<string>(retainedIds.Count);
            for (var index = 0; index < retainedIds.Count; index++)
            {
                var name = $"@retained{index}";
                names.Add(name);
                command.Parameters.AddWithValue(name, retainedIds[index]);
            }

            command.CommandText = $"UPDATE stations SET deleted_at_utc = @deletedAt WHERE deleted_at_utc IS NULL AND id NOT IN ({string.Join(",", names)});";
        }

        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
