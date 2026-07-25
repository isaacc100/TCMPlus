using Microsoft.Data.Sqlite;
using TCMPlus.Domain.Models;
using TCMPlus.Domain.Persistence;

namespace TCMPlus.Infrastructure.Persistence;

public sealed class SqliteMobileTeamRepository(SqliteConnectionFactory connectionFactory) : IMobileTeamRepository
{
    public async Task<IReadOnlyList<MobileTeam>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        var teams = new List<MobileTeam>();
        await using var connection = connectionFactory.OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = SelectTeams + " WHERE deleted_at_utc IS NULL ORDER BY callsign COLLATE NOCASE;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            teams.Add(ReadTeam(reader));
        }

        return teams;
    }

    public async Task<MobileTeam?> GetByIdAsync(Guid teamId, CancellationToken cancellationToken = default)
    {
        await using var connection = connectionFactory.OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = SelectTeams + " WHERE id = @id AND deleted_at_utc IS NULL LIMIT 1;";
        command.Parameters.AddWithValue("@id", teamId.ToString("N"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadTeam(reader) : null;
    }

    public async Task AddAsync(MobileTeam team, CancellationToken cancellationToken = default)
    {
        await using var connection = connectionFactory.OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO mobile_teams (id, callsign, note, is_deployed, deployment_location)
            VALUES (@id, @callsign, @note, @deployed, @location);
            """;
        Bind(command, team);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task UpdateAsync(MobileTeam team, CancellationToken cancellationToken = default)
    {
        await using var connection = connectionFactory.OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE mobile_teams
            SET callsign = @callsign,
                note = @note,
                is_deployed = @deployed,
                deployment_location = @location
            WHERE id = @id
              AND deleted_at_utc IS NULL;
            """;
        Bind(command, team);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task SoftDeleteAsync(Guid teamId, DateTimeOffset deletedAt, CancellationToken cancellationToken = default)
    {
        await using var connection = connectionFactory.OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE mobile_teams SET deleted_at_utc = @deletedAt WHERE id = @id AND deleted_at_utc IS NULL;";
        command.Parameters.AddWithValue("@id", teamId.ToString("N"));
        command.Parameters.AddWithValue("@deletedAt", deletedAt.UtcDateTime.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static void Bind(SqliteCommand command, MobileTeam team)
    {
        command.Parameters.AddWithValue("@id", team.Id.ToString("N"));
        command.Parameters.AddWithValue("@callsign", team.Callsign.Trim());
        command.Parameters.AddWithValue("@note", string.IsNullOrWhiteSpace(team.Note) ? DBNull.Value : team.Note.Trim());
        command.Parameters.AddWithValue("@deployed", team.IsDeployed ? 1 : 0);
        command.Parameters.AddWithValue("@location", string.IsNullOrWhiteSpace(team.DeploymentLocation) ? DBNull.Value : team.DeploymentLocation.Trim());
    }

    private static MobileTeam ReadTeam(SqliteDataReader reader) => new(
        Guid.Parse(reader.GetString(0)),
        reader.GetString(1),
        reader.IsDBNull(2) ? null : reader.GetString(2),
        reader.GetInt32(3) != 0,
        reader.IsDBNull(4) ? null : reader.GetString(4));

    private const string SelectTeams = "SELECT id, callsign, note, is_deployed, deployment_location FROM mobile_teams";
}
