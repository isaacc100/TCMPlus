using Microsoft.Data.Sqlite;
using TCMPlus.Domain.Models;
using TCMPlus.Domain.Persistence;

namespace TCMPlus.Infrastructure.Persistence;

public sealed class SqlitePatientRepository(SqliteConnectionFactory connectionFactory) : IPatientRepository
{
    public Task<IReadOnlyList<Patient>> GetAllActiveAsync(CancellationToken cancellationToken = default) =>
        GetPatientsAsync("WHERE current_station_id IS NOT NULL", cancellationToken);

    public Task<IReadOnlyList<Patient>> GetAllAsync(CancellationToken cancellationToken = default) =>
        GetPatientsAsync(string.Empty, cancellationToken);

    public async Task<int> GetNextPatientNumberAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = connectionFactory.OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COALESCE(MAX(patient_number), 0) + 1 FROM patients;";
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
    }

    public async Task<Patient?> GetByStationAsync(Guid stationId, CancellationToken cancellationToken = default)
    {
        await using var connection = connectionFactory.OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = SelectPatients + " WHERE current_station_id = @stationId LIMIT 1;";
        command.Parameters.AddWithValue("@stationId", stationId.ToString("N"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadPatient(reader) : null;
    }

    public async Task AddAsync(Patient patient, CancellationToken cancellationToken = default)
    {
        await using var connection = connectionFactory.OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO patients (uid, patient_number, added_at_utc, current_station_id, presenting_complaint, discharged_at_utc)
            VALUES (@uid, @number, @addedAt, @stationId, @complaint, @dischargedAt);
            """;
        BindPatient(command, patient);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<Patient?> DischargeFromStationAsync(Guid stationId, DateTimeOffset dischargedAt, CancellationToken cancellationToken = default)
    {
        await using var connection = connectionFactory.OpenConnection();
        using var transaction = connection.BeginTransaction();
        var patient = await GetByStationAsync(connection, transaction, stationId, cancellationToken);
        if (patient is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return null;
        }

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "UPDATE patients SET current_station_id = NULL, discharged_at_utc = @dischargedAt WHERE uid = @uid;";
        command.Parameters.AddWithValue("@dischargedAt", dischargedAt.UtcDateTime.ToString("O"));
        command.Parameters.AddWithValue("@uid", patient.Uid.ToString("N"));
        await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return patient with { CurrentStationId = null, DischargedAt = dischargedAt };
    }

    public async Task<PatientTransferResult> MoveAsync(Guid sourceStationId, Guid destinationStationId, bool swap, CancellationToken cancellationToken = default)
    {
        await using var connection = connectionFactory.OpenConnection();
        using var transaction = connection.BeginTransaction();
        var source = await GetByStationAsync(connection, transaction, sourceStationId, cancellationToken)
            ?? throw new InvalidOperationException("The source station no longer has a patient.");
        var destination = await GetByStationAsync(connection, transaction, destinationStationId, cancellationToken);
        if (destination is not null && !swap)
        {
            throw new InvalidOperationException("The destination station is occupied.");
        }

        await UpdateStationAsync(connection, transaction, source.Uid, destinationStationId, cancellationToken);
        if (destination is not null)
        {
            await UpdateStationAsync(connection, transaction, destination.Uid, sourceStationId, cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return new PatientTransferResult(source with { CurrentStationId = destinationStationId }, destination is null ? null : destination with { CurrentStationId = sourceStationId });
    }

    public async Task AddEventAsync(PatientEvent patientEvent, CancellationToken cancellationToken = default)
    {
        await using var connection = connectionFactory.OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO patient_events (id, patient_uid, patient_number, event_type, occurred_at_utc, from_station_name, to_station_name)
            VALUES (@id, @patientUid, @number, @type, @occurredAt, @from, @to);
            """;
        command.Parameters.AddWithValue("@id", patientEvent.Id.ToString("N"));
        command.Parameters.AddWithValue("@patientUid", patientEvent.PatientUid.ToString("N"));
        command.Parameters.AddWithValue("@number", patientEvent.PatientNumber);
        command.Parameters.AddWithValue("@type", patientEvent.Type.ToString());
        command.Parameters.AddWithValue("@occurredAt", patientEvent.OccurredAt.UtcDateTime.ToString("O"));
        command.Parameters.AddWithValue("@from", (object?)patientEvent.FromStationName ?? DBNull.Value);
        command.Parameters.AddWithValue("@to", (object?)patientEvent.ToStationName ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<PatientEvent>> GetAllEventsAsync(CancellationToken cancellationToken = default)
    {
        var events = new List<PatientEvent>();
        await using var connection = connectionFactory.OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, patient_uid, patient_number, event_type, occurred_at_utc, from_station_name, to_station_name FROM patient_events ORDER BY occurred_at_utc DESC;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            events.Add(new PatientEvent(Guid.Parse(reader.GetString(0)), Guid.Parse(reader.GetString(1)), reader.GetInt32(2), Enum.Parse<PatientEventType>(reader.GetString(3)), ParseTime(reader.GetString(4)), reader.IsDBNull(5) ? null : reader.GetString(5), reader.IsDBNull(6) ? null : reader.GetString(6)));
        }
        return events;
    }

    private async Task<IReadOnlyList<Patient>> GetPatientsAsync(string filter, CancellationToken cancellationToken)
    {
        var patients = new List<Patient>();
        await using var connection = connectionFactory.OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = SelectPatients + " " + filter + " ORDER BY patient_number;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) patients.Add(ReadPatient(reader));
        return patients;
    }

    private static async Task<Patient?> GetByStationAsync(SqliteConnection connection, SqliteTransaction transaction, Guid stationId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = SelectPatients + " WHERE current_station_id = @stationId LIMIT 1;";
        command.Parameters.AddWithValue("@stationId", stationId.ToString("N"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadPatient(reader) : null;
    }

    private static async Task UpdateStationAsync(SqliteConnection connection, SqliteTransaction transaction, Guid patientUid, Guid stationId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "UPDATE patients SET current_station_id = @stationId WHERE uid = @uid;";
        command.Parameters.AddWithValue("@stationId", stationId.ToString("N"));
        command.Parameters.AddWithValue("@uid", patientUid.ToString("N"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static void BindPatient(SqliteCommand command, Patient patient)
    {
        command.Parameters.AddWithValue("@uid", patient.Uid.ToString("N"));
        command.Parameters.AddWithValue("@number", patient.PatientNumber);
        command.Parameters.AddWithValue("@addedAt", patient.AddedAt.UtcDateTime.ToString("O"));
        command.Parameters.AddWithValue("@stationId", patient.CurrentStationId?.ToString("N") ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("@complaint", string.IsNullOrWhiteSpace(patient.PresentingComplaint) ? DBNull.Value : patient.PresentingComplaint.Trim());
        command.Parameters.AddWithValue("@dischargedAt", patient.DischargedAt?.UtcDateTime.ToString("O") ?? (object)DBNull.Value);
    }

    private static Patient ReadPatient(SqliteDataReader reader) => new(Guid.Parse(reader.GetString(0)), reader.GetInt32(1), ParseTime(reader.GetString(2)), reader.IsDBNull(3) ? null : Guid.Parse(reader.GetString(3)), reader.IsDBNull(4) ? null : reader.GetString(4), reader.IsDBNull(5) ? null : ParseTime(reader.GetString(5)));
    private static DateTimeOffset ParseTime(string value) => DateTimeOffset.Parse(value, null, System.Globalization.DateTimeStyles.RoundtripKind);
    private const string SelectPatients = "SELECT uid, patient_number, added_at_utc, current_station_id, presenting_complaint, discharged_at_utc FROM patients";
}
