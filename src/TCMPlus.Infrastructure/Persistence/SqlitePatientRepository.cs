using Microsoft.Data.Sqlite;
using TCMPlus.Domain.Models;
using TCMPlus.Domain.Persistence;

namespace TCMPlus.Infrastructure.Persistence;

public sealed class SqlitePatientRepository(SqliteConnectionFactory connectionFactory) : IPatientRepository
{
    public Task<IReadOnlyList<Patient>> GetAllActiveAsync(CancellationToken cancellationToken = default) =>
        GetPatientsAsync("WHERE discharged_at_utc IS NULL", cancellationToken);

    public Task<IReadOnlyList<Patient>> GetAllAsync(CancellationToken cancellationToken = default) =>
        GetPatientsAsync(string.Empty, cancellationToken);

    public async Task<int> GetNextPatientNumberAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = connectionFactory.OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COALESCE(MAX(patient_number), 0) + 1 FROM patients;";
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
    }

    public async Task<Patient?> GetByUidAsync(Guid patientUid, CancellationToken cancellationToken = default)
    {
        await using var connection = connectionFactory.OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = SelectPatients + " WHERE uid = @uid LIMIT 1;";
        command.Parameters.AddWithValue("@uid", patientUid.ToString("N"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadPatient(reader) : null;
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

    public async Task<Patient?> GetByMobileTeamAsync(Guid teamId, CancellationToken cancellationToken = default)
    {
        await using var connection = connectionFactory.OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = SelectPatients + " WHERE current_mobile_team_id = @teamId LIMIT 1;";
        command.Parameters.AddWithValue("@teamId", teamId.ToString("N"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadPatient(reader) : null;
    }

    public async Task AddAsync(Patient patient, CancellationToken cancellationToken = default)
    {
        await using var connection = connectionFactory.OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO patients (uid, patient_number, added_at_utc, current_station_id, presenting_complaint, discharged_at_utc, discharge_route, discharge_outcome, current_mobile_team_id)
            VALUES (@uid, @number, @addedAt, @stationId, @complaint, @dischargedAt, @route, @outcome, @teamId);
            """;
        BindPatient(command, patient);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task UpdateDetailsAsync(Patient patient, CancellationToken cancellationToken = default)
    {
        await using var connection = connectionFactory.OpenConnection();
        using var transaction = connection.BeginTransaction();

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE patients
                SET added_at_utc = @addedAt,
                    discharged_at_utc = @dischargedAt,
                    presenting_complaint = @complaint,
                    discharge_route = @route,
                    discharge_outcome = @outcome
                WHERE uid = @uid;
                """;
            BindPatient(command, patient);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await UpdateEventTimeAsync(connection, transaction, patient.Uid, PatientEventType.Added, patient.AddedAt, cancellationToken);
        if (patient.DischargedAt is not null)
        {
            await UpdateEventTimeAsync(connection, transaction, patient.Uid, PatientEventType.Discharged, patient.DischargedAt.Value, cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task UpdatePresentingComplaintAsync(IReadOnlyCollection<Guid> patientUids, string presentingComplaint, CancellationToken cancellationToken = default)
    {
        await using var connection = connectionFactory.OpenConnection();
        using var transaction = connection.BeginTransaction();
        foreach (var patientUid in patientUids)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "UPDATE patients SET presenting_complaint = @complaint WHERE uid = @uid;";
            command.Parameters.AddWithValue("@complaint", presentingComplaint);
            command.Parameters.AddWithValue("@uid", patientUid.ToString("N"));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task DeleteAsync(Guid patientUid, CancellationToken cancellationToken = default)
    {
        await using var connection = connectionFactory.OpenConnection();
        using var transaction = connection.BeginTransaction();

        await using (var events = connection.CreateCommand())
        {
            events.Transaction = transaction;
            events.CommandText = "DELETE FROM patient_events WHERE patient_uid = @uid;";
            events.Parameters.AddWithValue("@uid", patientUid.ToString("N"));
            await events.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var patient = connection.CreateCommand())
        {
            patient.Transaction = transaction;
            patient.CommandText = "DELETE FROM patients WHERE uid = @uid;";
            patient.Parameters.AddWithValue("@uid", patientUid.ToString("N"));
            await patient.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<Patient?> DischargeFromStationAsync(Guid stationId, DateTimeOffset dischargedAt, string? dischargeRoute, string? dischargeOutcome, CancellationToken cancellationToken = default)
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
        command.CommandText = "UPDATE patients SET current_station_id = NULL, discharged_at_utc = @dischargedAt, discharge_route = @route, discharge_outcome = @outcome WHERE uid = @uid;";
        command.Parameters.AddWithValue("@dischargedAt", dischargedAt.UtcDateTime.ToString("O"));
        command.Parameters.AddWithValue("@uid", patient.Uid.ToString("N"));
        command.Parameters.AddWithValue("@route", string.IsNullOrWhiteSpace(dischargeRoute) ? DBNull.Value : dischargeRoute.Trim());
        command.Parameters.AddWithValue("@outcome", string.IsNullOrWhiteSpace(dischargeOutcome) ? DBNull.Value : dischargeOutcome.Trim());
        await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return patient with { CurrentStationId = null, DischargedAt = dischargedAt, DischargeRoute = dischargeRoute, DischargeOutcome = dischargeOutcome };
    }

    public async Task<Patient?> DischargeAsync(Guid patientUid, DateTimeOffset dischargedAt, string? dischargeRoute, string? dischargeOutcome, CancellationToken cancellationToken = default)
    {
        await using var connection = connectionFactory.OpenConnection();
        using var transaction = connection.BeginTransaction();
        var patient = await GetByUidAsync(connection, transaction, patientUid, cancellationToken);
        if (patient is null || patient.DischargedAt is not null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return null;
        }

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE patients
            SET current_station_id = NULL,
                current_mobile_team_id = NULL,
                discharged_at_utc = @dischargedAt,
                discharge_route = @route,
                discharge_outcome = @outcome
            WHERE uid = @uid;
            """;
        command.Parameters.AddWithValue("@dischargedAt", dischargedAt.UtcDateTime.ToString("O"));
        command.Parameters.AddWithValue("@uid", patient.Uid.ToString("N"));
        command.Parameters.AddWithValue("@route", string.IsNullOrWhiteSpace(dischargeRoute) ? DBNull.Value : dischargeRoute.Trim());
        command.Parameters.AddWithValue("@outcome", string.IsNullOrWhiteSpace(dischargeOutcome) ? DBNull.Value : dischargeOutcome.Trim());
        await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return patient with
        {
            CurrentStationId = null,
            CurrentMobileTeamId = null,
            DischargedAt = dischargedAt,
            DischargeRoute = dischargeRoute,
            DischargeOutcome = dischargeOutcome
        };
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

    public async Task<PatientTransferResult> MoveAsync(Guid patientUid, PatientAssignment destination, bool swap, CancellationToken cancellationToken = default)
    {
        await using var connection = connectionFactory.OpenConnection();
        using var transaction = connection.BeginTransaction();
        var source = await GetByUidAsync(connection, transaction, patientUid, cancellationToken)
            ?? throw new InvalidOperationException("The patient no longer exists.");
        if (source.DischargedAt is not null)
        {
            throw new InvalidOperationException("Discharged patients cannot be transferred.");
        }

        var sourceAssignment = CurrentAssignment(source)
            ?? throw new InvalidOperationException("The patient is not assigned to a station or mobile team.");
        if (sourceAssignment == destination)
        {
            throw new InvalidOperationException("Choose a different destination.");
        }

        var destinationPatient = await GetByAssignmentAsync(connection, transaction, destination, cancellationToken);
        if (destinationPatient is not null && !swap)
        {
            throw new InvalidOperationException("The destination is occupied.");
        }

        await UpdateAssignmentAsync(connection, transaction, source.Uid, destination, cancellationToken);
        if (destinationPatient is not null)
        {
            await UpdateAssignmentAsync(connection, transaction, destinationPatient.Uid, sourceAssignment, cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return new PatientTransferResult(
            WithAssignment(source, destination),
            destinationPatient is null ? null : WithAssignment(destinationPatient, sourceAssignment));
    }

    public async Task AddEventAsync(PatientEvent patientEvent, CancellationToken cancellationToken = default)
    {
        await using var connection = connectionFactory.OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO patient_events (id, patient_uid, patient_number, event_type, occurred_at_utc, from_station_name, to_station_name, from_location_kind, to_location_kind)
            VALUES (@id, @patientUid, @number, @type, @occurredAt, @from, @to, @fromKind, @toKind);
            """;
        command.Parameters.AddWithValue("@id", patientEvent.Id.ToString("N"));
        command.Parameters.AddWithValue("@patientUid", patientEvent.PatientUid.ToString("N"));
        command.Parameters.AddWithValue("@number", patientEvent.PatientNumber);
        command.Parameters.AddWithValue("@type", patientEvent.Type.ToString());
        command.Parameters.AddWithValue("@occurredAt", patientEvent.OccurredAt.UtcDateTime.ToString("O"));
        command.Parameters.AddWithValue("@from", (object?)patientEvent.FromLocationName ?? DBNull.Value);
        command.Parameters.AddWithValue("@to", (object?)patientEvent.ToLocationName ?? DBNull.Value);
        command.Parameters.AddWithValue("@fromKind", patientEvent.FromLocationKind?.ToString() ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("@toKind", patientEvent.ToLocationKind?.ToString() ?? (object)DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<PatientEvent>> GetAllEventsAsync(CancellationToken cancellationToken = default)
    {
        var events = new List<PatientEvent>();
        await using var connection = connectionFactory.OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, patient_uid, patient_number, event_type, occurred_at_utc, from_station_name, to_station_name, from_location_kind, to_location_kind FROM patient_events ORDER BY occurred_at_utc DESC;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            events.Add(new PatientEvent(
                Guid.Parse(reader.GetString(0)),
                Guid.Parse(reader.GetString(1)),
                reader.GetInt32(2),
                Enum.Parse<PatientEventType>(reader.GetString(3)),
                ParseTime(reader.GetString(4)),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.IsDBNull(7) ? null : Enum.Parse<PatientAssignmentKind>(reader.GetString(7)),
                reader.IsDBNull(8) ? null : Enum.Parse<PatientAssignmentKind>(reader.GetString(8))));
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

    private static async Task<Patient?> GetByUidAsync(SqliteConnection connection, SqliteTransaction transaction, Guid patientUid, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = SelectPatients + " WHERE uid = @uid LIMIT 1;";
        command.Parameters.AddWithValue("@uid", patientUid.ToString("N"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadPatient(reader) : null;
    }

    private static async Task<Patient?> GetByAssignmentAsync(SqliteConnection connection, SqliteTransaction transaction, PatientAssignment assignment, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = assignment.Kind == PatientAssignmentKind.Station
            ? SelectPatients + " WHERE current_station_id = @id LIMIT 1;"
            : SelectPatients + " WHERE current_mobile_team_id = @id LIMIT 1;";
        command.Parameters.AddWithValue("@id", assignment.Id.ToString("N"));
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

    private static async Task UpdateAssignmentAsync(SqliteConnection connection, SqliteTransaction transaction, Guid patientUid, PatientAssignment assignment, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE patients
            SET current_station_id = @stationId,
                current_mobile_team_id = @teamId
            WHERE uid = @uid;
            """;
        command.Parameters.AddWithValue("@uid", patientUid.ToString("N"));
        command.Parameters.AddWithValue("@stationId", assignment.Kind == PatientAssignmentKind.Station ? assignment.Id.ToString("N") : DBNull.Value);
        command.Parameters.AddWithValue("@teamId", assignment.Kind == PatientAssignmentKind.MobileTeam ? assignment.Id.ToString("N") : DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task UpdateEventTimeAsync(SqliteConnection connection, SqliteTransaction transaction, Guid patientUid, PatientEventType type, DateTimeOffset occurredAt, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "UPDATE patient_events SET occurred_at_utc = @occurredAt WHERE patient_uid = @uid AND event_type = @type;";
        command.Parameters.AddWithValue("@occurredAt", occurredAt.UtcDateTime.ToString("O"));
        command.Parameters.AddWithValue("@uid", patientUid.ToString("N"));
        command.Parameters.AddWithValue("@type", type.ToString());
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static void BindPatient(SqliteCommand command, Patient patient)
    {
        command.Parameters.AddWithValue("@uid", patient.Uid.ToString("N"));
        command.Parameters.AddWithValue("@number", patient.PatientNumber);
        command.Parameters.AddWithValue("@addedAt", patient.AddedAt.UtcDateTime.ToString("O"));
        command.Parameters.AddWithValue("@stationId", patient.CurrentStationId?.ToString("N") ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("@teamId", patient.CurrentMobileTeamId?.ToString("N") ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("@complaint", string.IsNullOrWhiteSpace(patient.PresentingComplaint) ? DBNull.Value : patient.PresentingComplaint.Trim());
        command.Parameters.AddWithValue("@dischargedAt", patient.DischargedAt?.UtcDateTime.ToString("O") ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("@route", string.IsNullOrWhiteSpace(patient.DischargeRoute) ? DBNull.Value : patient.DischargeRoute.Trim());
        command.Parameters.AddWithValue("@outcome", string.IsNullOrWhiteSpace(patient.DischargeOutcome) ? DBNull.Value : patient.DischargeOutcome.Trim());
    }

    private static Patient ReadPatient(SqliteDataReader reader) => new(
        Guid.Parse(reader.GetString(0)),
        reader.GetInt32(1),
        ParseTime(reader.GetString(2)),
        reader.IsDBNull(3) ? null : Guid.Parse(reader.GetString(3)),
        reader.IsDBNull(4) ? null : reader.GetString(4),
        reader.IsDBNull(5) ? null : ParseTime(reader.GetString(5)),
        reader.IsDBNull(6) ? null : reader.GetString(6),
        reader.IsDBNull(7) ? null : reader.GetString(7),
        reader.IsDBNull(8) ? null : Guid.Parse(reader.GetString(8)));

    private static PatientAssignment? CurrentAssignment(Patient patient) =>
        patient.CurrentStationId is Guid stationId
            ? new PatientAssignment(PatientAssignmentKind.Station, stationId)
            : patient.CurrentMobileTeamId is Guid teamId
                ? new PatientAssignment(PatientAssignmentKind.MobileTeam, teamId)
                : null;

    private static Patient WithAssignment(Patient patient, PatientAssignment assignment) =>
        assignment.Kind == PatientAssignmentKind.Station
            ? patient with { CurrentStationId = assignment.Id, CurrentMobileTeamId = null }
            : patient with { CurrentStationId = null, CurrentMobileTeamId = assignment.Id };

    private static DateTimeOffset ParseTime(string value) => DateTimeOffset.Parse(value, null, System.Globalization.DateTimeStyles.RoundtripKind);
    private const string SelectPatients = "SELECT uid, patient_number, added_at_utc, current_station_id, presenting_complaint, discharged_at_utc, discharge_route, discharge_outcome, current_mobile_team_id FROM patients";
}
