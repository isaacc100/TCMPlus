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
        command.CommandText = "SELECT id, name, station_type, grid_x, grid_y, grid_width, grid_height FROM stations ORDER BY name, station_type;";

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
        "INSERT INTO stations (id, name, station_type, grid_x, grid_y, grid_width, grid_height) VALUES (@id, @name, @type, @gridX, @gridY, @gridWidth, @gridHeight);",
        station,
        cancellationToken);

    public Task UpdateAsync(Station station, CancellationToken cancellationToken = default) => ExecuteWriteAsync(
        "UPDATE stations SET name = @name, station_type = @type, grid_x = @gridX, grid_y = @gridY, grid_width = @gridWidth, grid_height = @gridHeight WHERE id = @id;",
        station,
        cancellationToken);

    public async Task DeleteAsync(Guid stationId, CancellationToken cancellationToken = default)
    {
        await using var connection = connectionFactory.OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM stations WHERE id = @id;";
        command.Parameters.AddWithValue("@id", stationId.ToString("N"));
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
