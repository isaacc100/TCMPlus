using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using TCMPlus.Infrastructure.Persistence;
using TCMPlus.Protocol;

namespace TCMPlus.Infrastructure.Networking;

public sealed class TerminalSecurityStore(SqliteConnectionFactory connectionFactory)
{
    private const int SaltBytes = 16;
    private const int HashBytes = 32;
    private const int Iterations = 310_000;
    private const string PasswordAlphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<TerminalCredential> CreateAsync(
        string terminalName,
        DateTimeOffset expiresAt,
        int protocolVersion = TerminalProtocol.CurrentVersion,
        CancellationToken cancellationToken = default)
    {
        var name = terminalName.Trim();
        if (name.Length is < 2 or > 48)
        {
            throw new InvalidOperationException("Terminal names must contain between 2 and 48 characters.");
        }

        if (name.Any(character => char.IsControl(character)))
        {
            throw new InvalidOperationException("Terminal names cannot contain control characters.");
        }

        var now = DateTimeOffset.UtcNow;
        if (expiresAt <= now)
        {
            throw new InvalidOperationException("Terminal access must expire in the future.");
        }

        var password = GeneratePassword();
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var hash = HashPassword(password, salt);
        var registration = new TerminalRegistration(Guid.NewGuid(), name, now, expiresAt, null, protocolVersion);

        await using var connection = connectionFactory.OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO terminal_registrations
                (id, terminal_name, password_salt, password_hash, created_at_utc, expires_at_utc, revoked_at_utc, protocol_version)
            VALUES
                (@id, @name, @salt, @hash, @created, @expires, NULL, @protocol);
            """;
        command.Parameters.AddWithValue("@id", registration.Id.ToString("N"));
        command.Parameters.AddWithValue("@name", registration.Name);
        command.Parameters.AddWithValue("@salt", Convert.ToBase64String(salt));
        command.Parameters.AddWithValue("@hash", Convert.ToBase64String(hash));
        command.Parameters.AddWithValue("@created", registration.CreatedAt.UtcDateTime.ToString("O"));
        command.Parameters.AddWithValue("@expires", registration.ExpiresAt.UtcDateTime.ToString("O"));
        command.Parameters.AddWithValue("@protocol", registration.ProtocolVersion);
        try
        {
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode == 19)
        {
            throw new InvalidOperationException("An active terminal with this name already exists.", exception);
        }

        return new TerminalCredential(registration, password);
    }

    public async Task<TerminalRegistration?> VerifyAsync(
        string terminalName,
        string password,
        int protocolVersion,
        CancellationToken cancellationToken = default)
    {
        if (protocolVersion != TerminalProtocol.CurrentVersion || string.IsNullOrWhiteSpace(terminalName) || password.Length > 128)
        {
            return null;
        }

        await using var connection = connectionFactory.OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, terminal_name, password_salt, password_hash, created_at_utc, expires_at_utc, revoked_at_utc, protocol_version
            FROM terminal_registrations
            WHERE terminal_name = @name COLLATE NOCASE
            ORDER BY created_at_utc DESC
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("@name", terminalName.Trim());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            PerformDummyPasswordHash(password);
            return null;
        }

        var registration = ReadRegistration(reader);
        var salt = Convert.FromBase64String(reader.GetString(2));
        var expected = Convert.FromBase64String(reader.GetString(3));
        var actual = HashPassword(password, salt);
        var valid = CryptographicOperations.FixedTimeEquals(actual, expected)
            && registration.RevokedAt is null
            && registration.ExpiresAt > DateTimeOffset.UtcNow
            && registration.ProtocolVersion == protocolVersion;
        return valid ? registration : null;
    }

    public async Task<IReadOnlyList<TerminalRegistration>> GetRegistrationsAsync(CancellationToken cancellationToken = default)
    {
        var registrations = new List<TerminalRegistration>();
        await using var connection = connectionFactory.OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, terminal_name, password_salt, password_hash, created_at_utc, expires_at_utc, revoked_at_utc, protocol_version
            FROM terminal_registrations
            ORDER BY created_at_utc DESC;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            registrations.Add(ReadRegistration(reader));
        }

        return registrations;
    }

    public Task RevokeAsync(Guid terminalId, CancellationToken cancellationToken = default) =>
        RevokeWhereAsync("id = @value", terminalId.ToString("N"), cancellationToken);

    public async Task RevokeAllAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = connectionFactory.OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE terminal_registrations SET revoked_at_utc = @now WHERE revoked_at_utc IS NULL;";
        command.Parameters.AddWithValue("@now", DateTimeOffset.UtcNow.UtcDateTime.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<PendingCommand> BeginCommandAsync(
        TerminalRegistration terminal,
        TerminalCommandRequest request,
        string? target,
        CancellationToken cancellationToken = default)
    {
        await using var connection = connectionFactory.OpenConnection();
        await using var transaction = connection.BeginTransaction();

        await using (var existing = connection.CreateCommand())
        {
            existing.Transaction = transaction;
            existing.CommandText = "SELECT sequence, status, response_json FROM terminal_command_audit WHERE request_id = @requestId LIMIT 1;";
            existing.Parameters.AddWithValue("@requestId", request.RequestId.ToString("N"));
            await using var reader = await existing.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                var sequence = reader.GetInt64(0);
                var status = reader.GetString(1);
                var responseJson = reader.IsDBNull(2) ? null : reader.GetString(2);
                await transaction.CommitAsync(cancellationToken);
                if (responseJson is not null)
                {
                    return new PendingCommand(sequence, JsonSerializer.Deserialize<TerminalCommandResponse>(responseJson, JsonOptions));
                }

                return new PendingCommand(sequence, new TerminalCommandResponse(
                    request.RequestId,
                    TerminalCommandStatus.Rejected,
                    sequence,
                    DateTimeOffset.UtcNow,
                    "unknown_outcome",
                    status == "Pending"
                        ? "The host received this command but its outcome is unknown. Refresh before deciding whether to retry."
                        : "This command has already been processed."));
            }
        }

        await using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = """
            INSERT INTO terminal_command_audit
                (request_id, terminal_id, terminal_name, received_at_utc, operation, target, status)
            VALUES
                (@requestId, @terminalId, @terminalName, @receivedAt, @operation, @target, 'Pending');
            SELECT last_insert_rowid();
            """;
        insert.Parameters.AddWithValue("@requestId", request.RequestId.ToString("N"));
        insert.Parameters.AddWithValue("@terminalId", terminal.Id.ToString("N"));
        insert.Parameters.AddWithValue("@terminalName", terminal.Name);
        insert.Parameters.AddWithValue("@receivedAt", DateTimeOffset.UtcNow.UtcDateTime.ToString("O"));
        insert.Parameters.AddWithValue("@operation", request.Kind.ToString());
        insert.Parameters.AddWithValue("@target", target is null ? DBNull.Value : target);
        var assignedSequence = (long)(await insert.ExecuteScalarAsync(cancellationToken)
            ?? throw new InvalidOperationException("The command sequence could not be allocated."));
        await transaction.CommitAsync(cancellationToken);
        return new PendingCommand(assignedSequence, null);
    }

    public async Task CompleteCommandAsync(
        TerminalCommandResponse response,
        CancellationToken cancellationToken = default)
    {
        var responseJson = JsonSerializer.Serialize(response, JsonOptions);
        await using var connection = connectionFactory.OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE terminal_command_audit
            SET processed_at_utc = @processedAt,
                status = @status,
                rejection_reason = @reason,
                response_json = @response
            WHERE request_id = @requestId
              AND status = 'Pending';
            """;
        command.Parameters.AddWithValue("@processedAt", response.ProcessedAt.UtcDateTime.ToString("O"));
        command.Parameters.AddWithValue("@status", response.Status.ToString());
        command.Parameters.AddWithValue("@reason", response.Message is null ? DBNull.Value : response.Message);
        command.Parameters.AddWithValue("@response", responseJson);
        command.Parameters.AddWithValue("@requestId", response.RequestId.ToString("N"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<TerminalAuditEntry>> GetAuditAsync(int limit = 200, CancellationToken cancellationToken = default)
    {
        var entries = new List<TerminalAuditEntry>();
        await using var connection = connectionFactory.OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT sequence, request_id, terminal_id, terminal_name, COALESCE(processed_at_utc, received_at_utc),
                   operation, target, status, rejection_reason
            FROM terminal_command_audit
            ORDER BY sequence DESC
            LIMIT @limit;
            """;
        command.Parameters.AddWithValue("@limit", Math.Clamp(limit, 1, 1000));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (!Enum.TryParse<TerminalCommandKind>(reader.GetString(5), out var operation)
                || !Enum.TryParse<TerminalCommandStatus>(reader.GetString(7), out var status))
            {
                continue;
            }

            entries.Add(new TerminalAuditEntry(
                reader.GetInt64(0),
                Guid.Parse(reader.GetString(1)),
                Guid.Parse(reader.GetString(2)),
                reader.GetString(3),
                DateTimeOffset.Parse(reader.GetString(4)),
                operation,
                reader.IsDBNull(6) ? null : reader.GetString(6),
                status,
                reader.IsDBNull(8) ? null : reader.GetString(8)));
        }

        return entries;
    }

    public async Task<long> GetCurrentSequenceAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = connectionFactory.OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COALESCE(MAX(sequence), 0) FROM terminal_command_audit;";
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken));
    }

    private async Task RevokeWhereAsync(string predicate, string value, CancellationToken cancellationToken)
    {
        await using var connection = connectionFactory.OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = $"UPDATE terminal_registrations SET revoked_at_utc = @now WHERE {predicate} AND revoked_at_utc IS NULL;";
        command.Parameters.AddWithValue("@now", DateTimeOffset.UtcNow.UtcDateTime.ToString("O"));
        command.Parameters.AddWithValue("@value", value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static TerminalRegistration ReadRegistration(SqliteDataReader reader) => new(
        Guid.Parse(reader.GetString(0)),
        reader.GetString(1),
        DateTimeOffset.Parse(reader.GetString(4)),
        DateTimeOffset.Parse(reader.GetString(5)),
        reader.IsDBNull(6) ? null : DateTimeOffset.Parse(reader.GetString(6)),
        reader.GetInt32(7));

    private static string GeneratePassword()
    {
        Span<char> characters = stackalloc char[14];
        var position = 0;
        for (var group = 0; group < 3; group++)
        {
            if (group > 0)
            {
                characters[position++] = '-';
            }

            for (var index = 0; index < 4; index++)
            {
                characters[position++] = PasswordAlphabet[RandomNumberGenerator.GetInt32(PasswordAlphabet.Length)];
            }
        }

        return new string(characters[..position]);
    }

    private static byte[] HashPassword(string password, byte[] salt) =>
        Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, HashBytes);

    private static void PerformDummyPasswordHash(string password)
    {
        var dummySalt = SHA256.HashData(Encoding.UTF8.GetBytes("TCM+ terminal authentication timing salt"))[..SaltBytes];
        _ = HashPassword(password, dummySalt);
    }
}

public sealed record PendingCommand(long Sequence, TerminalCommandResponse? ExistingResponse);
