using Microsoft.Data.Sqlite;
using TCMPlus.Domain.Models;
using TCMPlus.Domain.Persistence;

namespace TCMPlus.Infrastructure.Persistence;

public sealed class SqliteStationRepository(SqliteConnectionFactory connectionFactory) : IStationRepository
{
    public async Task<IReadOnlyList<Station>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        var stations = new List<Station>();
        await using var connection = connectionFactory.OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, name, station_type, grid_x, grid_y, grid_width, grid_height FROM stations WHERE deleted_at_utc IS NULL ORDER BY sort_order, name, station_type;";

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            stations.Add(new Station(
                Guid.Parse(reader.GetString(0)),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetDouble(3),
                reader.GetDouble(4),
                reader.GetDouble(5),
                reader.GetDouble(6)));
        }

        return stations;
    }

    public Task AddAsync(Station station, CancellationToken cancellationToken = default) => ExecuteWriteAsync(
        """
        INSERT INTO stations (id, name, station_type, grid_x, grid_y, grid_width, grid_height, sort_order)
        VALUES (@id, @name, @type, @gridX, @gridY, @gridWidth, @gridHeight, (SELECT COALESCE(MAX(sort_order), 0) + 1 FROM stations));
        """,
        station,
        cancellationToken);

    public Task UpdateAsync(Station station, CancellationToken cancellationToken = default) => ExecuteWriteAsync(
        "UPDATE stations SET name = @name, station_type = @type, grid_x = @gridX, grid_y = @gridY, grid_width = @gridWidth, grid_height = @gridHeight WHERE id = @id AND deleted_at_utc IS NULL;",
        station,
        cancellationToken);

    public async Task UpdateOrderAsync(IReadOnlyList<Guid> stationIds, CancellationToken cancellationToken = default)
    {
        await using var connection = connectionFactory.OpenConnection();
        using var transaction = connection.BeginTransaction();
        for (var index = 0; index < stationIds.Count; index++)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "UPDATE stations SET sort_order = @sortOrder WHERE id = @id AND deleted_at_utc IS NULL;";
            command.Parameters.AddWithValue("@sortOrder", index + 1);
            command.Parameters.AddWithValue("@id", stationIds[index].ToString("N"));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task SoftDeleteAsync(Guid stationId, DateTimeOffset deletedAt, CancellationToken cancellationToken = default)
    {
        await using var connection = connectionFactory.OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE stations SET deleted_at_utc = @deletedAt WHERE id = @id AND deleted_at_utc IS NULL;";
        command.Parameters.AddWithValue("@id", stationId.ToString("N"));
        command.Parameters.AddWithValue("@deletedAt", deletedAt.UtcDateTime.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task ExecuteWriteAsync(string sql, Station station, CancellationToken cancellationToken)
    {
        await using var connection = connectionFactory.OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("@id", station.Id.ToString("N"));
        command.Parameters.AddWithValue("@name", station.Name.Trim());
        command.Parameters.AddWithValue("@type", station.Type.Trim());
        command.Parameters.AddWithValue("@gridX", station.GridX);
        command.Parameters.AddWithValue("@gridY", station.GridY);
        command.Parameters.AddWithValue("@gridWidth", station.GridWidth);
        command.Parameters.AddWithValue("@gridHeight", station.GridHeight);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
