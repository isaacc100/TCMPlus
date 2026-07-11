using TCMPlus.Domain.Models;
using TCMPlus.Domain.Persistence;

namespace TCMPlus.Infrastructure.Persistence;

public sealed class SqlitePatientRepository(SqliteConnectionFactory connectionFactory) : IPatientRepository
{
    public async Task<IReadOnlyList<Patient>> GetAllActiveAsync(CancellationToken cancellationToken = default)
    {
        var patients = new List<Patient>();
        await using var connection = connectionFactory.OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT uid, added_at_utc, current_station_id FROM patients WHERE current_station_id IS NOT NULL;";

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            patients.Add(ReadPatient(reader));
        }

        return patients;
    }

    public async Task<int> GetDischargedCountAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = connectionFactory.OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM patients WHERE current_station_id IS NULL;";
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
    }

    public async Task<Patient?> GetByStationAsync(Guid stationId, CancellationToken cancellationToken = default)
    {
        await using var connection = connectionFactory.OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT uid, added_at_utc, current_station_id FROM patients WHERE current_station_id = @stationId LIMIT 1;";
        command.Parameters.AddWithValue("@stationId", stationId.ToString("N"));

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadPatient(reader) : null;
    }

    public async Task AddAsync(Patient patient, CancellationToken cancellationToken = default)
    {
        await using var connection = connectionFactory.OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO patients (uid, added_at_utc, current_station_id) VALUES (@uid, @addedAt, @stationId);";
        command.Parameters.AddWithValue("@uid", patient.Uid.ToString("N"));
        command.Parameters.AddWithValue("@addedAt", patient.AddedAt.UtcDateTime.ToString("O"));
        command.Parameters.AddWithValue("@stationId", patient.CurrentStationId!.Value.ToString("N"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task DischargeFromStationAsync(Guid stationId, CancellationToken cancellationToken = default)
    {
        await using var connection = connectionFactory.OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE patients SET current_station_id = NULL WHERE current_station_id = @stationId;";
        command.Parameters.AddWithValue("@stationId", stationId.ToString("N"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static Patient ReadPatient(Microsoft.Data.Sqlite.SqliteDataReader reader) => new(
        Guid.Parse(reader.GetString(0)),
        DateTimeOffset.Parse(reader.GetString(1), null, System.Globalization.DateTimeStyles.RoundtripKind),
        reader.IsDBNull(2) ? null : Guid.Parse(reader.GetString(2)));
}
