using TCMPlus.Domain.Models;
using TCMPlus.Domain.Persistence;

namespace TCMPlus.Infrastructure.Persistence;

public sealed class SqliteTcSettingsRepository(SqliteConnectionFactory connectionFactory) : ITcSettingsRepository
{
    public async Task<TcSessionSettings> GetAsync(CancellationToken cancellationToken = default)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        await using var connection = connectionFactory.OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT key, value FROM session_settings WHERE key IN ('pin_salt', 'pin_hash');";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            values[reader.GetString(0)] = reader.GetString(1);
        }

        return new TcSessionSettings(
            values.GetValueOrDefault("pin_salt"),
            values.GetValueOrDefault("pin_hash"));
    }

    public async Task SaveAsync(TcSessionSettings settings, CancellationToken cancellationToken = default)
    {
        await using var connection = connectionFactory.OpenConnection();
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await UpsertAsync(connection, "pin_salt", settings.PinSalt ?? string.Empty, cancellationToken);
        await UpsertAsync(connection, "pin_hash", settings.PinHash ?? string.Empty, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private static async Task UpsertAsync(Microsoft.Data.Sqlite.SqliteConnection connection, string key, string value, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO session_settings (key, value) VALUES (@key, @value) ON CONFLICT(key) DO UPDATE SET value = excluded.value;";
        command.Parameters.AddWithValue("@key", key);
        command.Parameters.AddWithValue("@value", value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
