using Microsoft.Data.Sqlite;
using TCMPlus.Infrastructure.Sessions;

namespace TCMPlus.Tests;

public sealed class EncryptedSessionStoreTests : IDisposable
{
    private const string Password = "password1";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "TCMPlusTests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Seals_sessions_to_an_encrypted_tcm_file_that_can_be_reopened()
    {
        var store = new EncryptedSessionStore(_root);
        var session = await store.CreateAsync("Night shift", Password);
        await CreateDatabaseAsync(session.DatabasePath, "initial");

        await store.SealAsync(session, Password);

        var entry = Assert.Single(await store.GetRecentAsync());
        Assert.True(File.Exists(entry.FilePath));
        Assert.False(Directory.Exists(session.DirectoryPath));
        Assert.Equal("TCM1", System.Text.Encoding.ASCII.GetString((await File.ReadAllBytesAsync(entry.FilePath))[..4]));

        var reopened = await store.OpenAsync(entry, Password);
        Assert.Equal("initial", await ReadMarkerAsync(reopened.DatabasePath));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.OpenAsync(entry, "wrongpass"));
    }

    [Fact]
    public async Task Autosave_refreshes_the_encrypted_copy_without_removing_the_working_database()
    {
        var store = new EncryptedSessionStore(_root);
        var session = await store.CreateAsync("Autosave shift", Password);
        await CreateDatabaseAsync(session.DatabasePath, "initial");
        await store.AutosaveAsync(session, Password);

        await WriteMarkerAsync(session.DatabasePath, "latest");
        await store.AutosaveAsync(session, Password);

        Assert.True(File.Exists(session.DatabasePath));
        var entry = Assert.Single(await store.GetRecentAsync());
        Directory.Delete(session.DirectoryPath, true);

        var reopened = await store.OpenAsync(entry, Password);
        Assert.Equal("latest", await ReadMarkerAsync(reopened.DatabasePath));
    }

    [Fact]
    public async Task Recovers_a_corrupt_catalog_from_session_files_and_the_working_database()
    {
        var store = new EncryptedSessionStore(_root);
        var session = await store.CreateAsync("Recovered shift name", Password);
        await CreateDatabaseAsync(session.DatabasePath, "current");
        await store.AutosaveAsync(session, Password);
        var expectedCreatedAt = new DateTimeOffset(Directory.GetCreationTimeUtc(session.DirectoryPath), TimeSpan.Zero);
        await File.WriteAllTextAsync(Path.Combine(_root, "session-catalog.json"), "");

        var recovered = Assert.Single(await new EncryptedSessionStore(_root).GetRecentAsync());

        Assert.Equal(session.Id, recovered.Id);
        Assert.Equal("Recovered shift name", recovered.ShiftName);
        Assert.Equal(expectedCreatedAt, recovered.CreatedAt);
        Assert.True(new FileInfo(Path.Combine(_root, "session-catalog.json")).Length > 0);
    }

    [Fact]
    public async Task Keeps_a_newer_healthy_working_database_after_an_abrupt_shutdown()
    {
        var store = new EncryptedSessionStore(_root);
        var session = await store.CreateAsync("Interrupted shift", Password);
        await CreateDatabaseAsync(session.DatabasePath, "autosaved");
        await store.AutosaveAsync(session, Password);
        var entry = Assert.Single(await store.GetRecentAsync());

        await WriteMarkerAsync(session.DatabasePath, "not yet autosaved");
        File.SetLastWriteTimeUtc(session.DatabasePath, File.GetLastWriteTimeUtc(entry.FilePath).AddMinutes(1));

        var reopened = await new EncryptedSessionStore(_root).OpenAsync(entry, Password);

        Assert.Equal("not yet autosaved", await ReadMarkerAsync(reopened.DatabasePath));
    }

    [Fact]
    public async Task Autosave_promotes_a_recovered_legacy_session_to_an_encrypted_session()
    {
        var id = Guid.NewGuid();
        var legacyDirectory = Path.Combine(_root, "Sessions", $"20260724-120000-Legacy-{id:N}");
        Directory.CreateDirectory(legacyDirectory);
        var legacyDatabase = Path.Combine(legacyDirectory, "tcm.sqlite");
        await CreateDatabaseAsync(legacyDatabase, "legacy");
        var store = new EncryptedSessionStore(_root);
        var legacyEntry = Assert.Single(await store.GetRecentAsync());
        Assert.True(legacyEntry.IsLegacy);

        var session = await store.OpenAsync(legacyEntry, Password);
        await WriteMarkerAsync(session.DatabasePath, "encrypted autosave");
        await store.AutosaveAsync(session, Password);

        var encryptedEntry = Assert.Single(await store.GetRecentAsync());
        Assert.False(encryptedEntry.IsLegacy);
        Assert.EndsWith(".tcm", encryptedEntry.FilePath, StringComparison.OrdinalIgnoreCase);
        Directory.Delete(session.DirectoryPath, true);
        var reopened = await store.OpenAsync(encryptedEntry, Password);
        Assert.Equal("encrypted autosave", await ReadMarkerAsync(reopened.DatabasePath));
    }

    private static async Task CreateDatabaseAsync(string path, string marker)
    {
        var connectionString = new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString();
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE session_settings (key TEXT PRIMARY KEY NOT NULL, value TEXT NOT NULL);
            INSERT INTO session_settings (key, value) VALUES ('shift_name', 'Recovered shift name');
            CREATE TABLE marker (value TEXT NOT NULL);
            INSERT INTO marker (value) VALUES (@value);
            """;
        command.Parameters.AddWithValue("@value", marker);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task WriteMarkerAsync(string path, string marker)
    {
        var connectionString = new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString();
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE marker SET value = @value;";
        command.Parameters.AddWithValue("@value", marker);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<string> ReadMarkerAsync(string path)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString();
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM marker LIMIT 1;";
        return (string)(await command.ExecuteScalarAsync())!;
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, true);
        }
    }
}
