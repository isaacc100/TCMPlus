using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using TCMPlus.Domain.Models;

namespace TCMPlus.Infrastructure.Sessions;

public sealed class EncryptedSessionStore
{
    private const int SaltLength = 32;
    private const int NonceLength = 12;
    private const int TagLength = 16;
    private const int KeyLength = 32;
    private const int Iterations = 300_000;

    private readonly string _root;
    private readonly string _sessions;
    private readonly string _working;
    private readonly string _catalogPath;
    private readonly string _catalogBackupPath;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public EncryptedSessionStore(string? root = null)
    {
        _root = root ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TCMPlus");
        _sessions = Path.Combine(_root, "Sessions");
        _working = Path.Combine(_root, "Working");
        _catalogPath = Path.Combine(_root, "session-catalog.json");
        _catalogBackupPath = _catalogPath + ".bak";
        Directory.CreateDirectory(_sessions);
        Directory.CreateDirectory(_working);
    }

    public async Task<IReadOnlyList<SessionCatalogEntry>> GetRecentAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var catalog = await ReadAndRecoverCatalogAsync(cancellationToken);
            return catalog.OrderByDescending(item => item.LastOpenedAt).ToList();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<SessionDescriptor> CreateAsync(string shiftName, string password, CancellationToken cancellationToken = default)
    {
        ValidatePassword(password);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var id = Guid.NewGuid();
            var now = DateTimeOffset.UtcNow;
            var directory = Path.Combine(_working, id.ToString("N"));
            Directory.CreateDirectory(directory);
            var descriptor = new SessionDescriptor(id, now, shiftName.Trim(), directory, Path.Combine(directory, "tcm.sqlite"));
            var catalog = await ReadAndRecoverCatalogAsync(cancellationToken);
            catalog.Add(new SessionCatalogEntry(id, descriptor.ShiftName, now, now, GetFilePath(id)));
            await WriteCatalogAsync(catalog, cancellationToken);
            return descriptor;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<SessionDescriptor> OpenAsync(SessionCatalogEntry entry, string password, CancellationToken cancellationToken = default)
    {
        ValidatePassword(password);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var directory = Path.Combine(_working, entry.Id.ToString("N"));
            Directory.CreateDirectory(directory);
            var database = Path.Combine(directory, "tcm.sqlite");

            if (entry.IsLegacy)
            {
                File.Copy(entry.FilePath, database, true);
            }
            else
            {
                await RestoreEncryptedSessionAsync(entry, database, password, cancellationToken);
            }

            var catalog = await ReadAndRecoverCatalogAsync(cancellationToken);
            var index = catalog.FindIndex(item => item.Id == entry.Id);
            if (index >= 0)
            {
                catalog[index] = catalog[index] with { LastOpenedAt = DateTimeOffset.UtcNow };
                await WriteCatalogAsync(catalog, cancellationToken);
            }

            return new SessionDescriptor(entry.Id, entry.CreatedAt, entry.ShiftName, directory, database);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task AutosaveAsync(SessionDescriptor session, string password, CancellationToken cancellationToken = default)
    {
        ValidatePassword(password);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await SaveEncryptedSnapshotAsync(session, password, cancellationToken);
            await PromoteCatalogEntryAsync(session.Id, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SealAsync(SessionDescriptor session, string password, CancellationToken cancellationToken = default)
    {
        ValidatePassword(password);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!File.Exists(session.DatabasePath))
            {
                return;
            }

            await SaveEncryptedSnapshotAsync(session, password, cancellationToken);
            await PromoteCatalogEntryAsync(session.Id, cancellationToken);

            if (IsWorkingDirectory(session.DirectoryPath))
            {
                SqliteConnection.ClearAllPools();
                await DeleteWorkspaceAsync(session.DirectoryPath, cancellationToken);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task PromoteCatalogEntryAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        var catalog = await ReadAndRecoverCatalogAsync(cancellationToken);
        var index = catalog.FindIndex(item => item.Id == sessionId);
        if (index < 0)
        {
            return;
        }

        var encryptedPath = GetFilePath(sessionId);
        if (!catalog[index].IsLegacy && string.Equals(catalog[index].FilePath, encryptedPath, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        catalog[index] = catalog[index] with { FilePath = encryptedPath, IsLegacy = false };
        await WriteCatalogAsync(catalog, cancellationToken);
    }

    public async Task RenameAsync(SessionCatalogEntry entry, string name, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var catalog = await ReadAndRecoverCatalogAsync(cancellationToken);
            var index = catalog.FindIndex(item => item.Id == entry.Id);
            if (index >= 0)
            {
                catalog[index] = catalog[index] with { ShiftName = name.Trim() };
                await WriteCatalogAsync(catalog, cancellationToken);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task DeleteAsync(SessionCatalogEntry entry, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var encryptedPath = GetFilePath(entry.Id);
            foreach (var path in new[] { entry.FilePath, encryptedPath }.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }

            var workingDirectory = Path.Combine(_working, entry.Id.ToString("N"));
            if (Directory.Exists(workingDirectory))
            {
                SqliteConnection.ClearAllPools();
                await DeleteWorkspaceAsync(workingDirectory, cancellationToken);
            }

            var catalog = await ReadAndRecoverCatalogAsync(cancellationToken);
            catalog.RemoveAll(item => item.Id == entry.Id);
            await WriteCatalogAsync(catalog, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ExportAsync(SessionCatalogEntry entry, string destination, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var encryptedPath = GetFilePath(entry.Id);
            var source = File.Exists(encryptedPath) ? encryptedPath : entry.FilePath;
            File.Copy(source, destination, true);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task RestoreEncryptedSessionAsync(
        SessionCatalogEntry entry,
        string database,
        string password,
        CancellationToken cancellationToken)
    {
        var restoredDatabase = database + $".{Guid.NewGuid():N}.restore";
        try
        {
            await DecryptAsync(entry.FilePath, restoredDatabase, password, cancellationToken);
            if (!await IsHealthySqliteDatabaseAsync(restoredDatabase, cancellationToken))
            {
                throw new InvalidOperationException("This session contains an invalid database.");
            }

            var workingCopyIsNewer = File.Exists(database)
                && File.GetLastWriteTimeUtc(database) > File.GetLastWriteTimeUtc(entry.FilePath)
                && await IsHealthySqliteDatabaseAsync(database, cancellationToken);

            if (!workingCopyIsNewer)
            {
                File.Move(restoredDatabase, database, true);
            }
        }
        finally
        {
            TryDeleteFile(restoredDatabase);
        }
    }

    private async Task SaveEncryptedSnapshotAsync(
        SessionDescriptor session,
        string password,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(session.DatabasePath))
        {
            return;
        }

        var destination = GetFilePath(session.Id);
        var encryptedTemporary = destination + $".{Guid.NewGuid():N}.tmp";
        var databaseSnapshot = Path.Combine(_working, $"{session.Id:N}.{Guid.NewGuid():N}.snapshot.sqlite");
        try
        {
            await CreateConsistentSnapshotAsync(session.DatabasePath, databaseSnapshot, cancellationToken);
            await EncryptAsync(databaseSnapshot, encryptedTemporary, password, cancellationToken);
            File.Move(encryptedTemporary, destination, true);
        }
        finally
        {
            TryDeleteFile(encryptedTemporary);
            TryDeleteFile(databaseSnapshot);
        }
    }

    private static async Task CreateConsistentSnapshotAsync(
        string sourcePath,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        var sourceConnectionString = new SqliteConnectionStringBuilder
        {
            DataSource = sourcePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString();
        var destinationConnectionString = new SqliteConnectionStringBuilder
        {
            DataSource = destinationPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false
        }.ToString();

        await using var source = new SqliteConnection(sourceConnectionString);
        await using var destination = new SqliteConnection(destinationConnectionString);
        await source.OpenAsync(cancellationToken);
        await destination.OpenAsync(cancellationToken);
        source.BackupDatabase(destination);
    }

    private async Task<List<SessionCatalogEntry>> ReadAndRecoverCatalogAsync(CancellationToken cancellationToken)
    {
        var primary = await TryReadCatalogAsync(_catalogPath, cancellationToken);
        var catalog = primary ?? await TryReadCatalogAsync(_catalogBackupPath, cancellationToken) ?? [];
        var recovered = await RecoverCatalogEntriesAsync(catalog, cancellationToken);

        if (primary is null || !CatalogsMatch(catalog, recovered))
        {
            await WriteCatalogAsync(recovered, cancellationToken);
        }

        return recovered;
    }

    private async Task<List<SessionCatalogEntry>> RecoverCatalogEntriesAsync(
        IEnumerable<SessionCatalogEntry> existing,
        CancellationToken cancellationToken)
    {
        var entries = existing
            .Where(item => item is not null)
            .GroupBy(item => item.Id)
            .ToDictionary(group => group.Key, group => group.OrderByDescending(item => item.LastOpenedAt).First());

        foreach (var filePath in Directory.EnumerateFiles(_sessions, "*.tcm", SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Guid.TryParseExact(Path.GetFileNameWithoutExtension(filePath), "N", out var id))
            {
                continue;
            }

            var file = new FileInfo(filePath);
            var workingDatabase = Path.Combine(_working, id.ToString("N"), "tcm.sqlite");
            var shiftName = await TryReadShiftNameAsync(workingDatabase, cancellationToken);
            var createdAt = new DateTimeOffset(file.CreationTimeUtc, TimeSpan.Zero);
            var lastOpenedAt = new DateTimeOffset(file.LastWriteTimeUtc, TimeSpan.Zero);
            if (File.Exists(workingDatabase))
            {
                createdAt = new DateTimeOffset(Directory.GetCreationTimeUtc(Path.GetDirectoryName(workingDatabase)!), TimeSpan.Zero);
                lastOpenedAt = Max(lastOpenedAt, new DateTimeOffset(File.GetLastWriteTimeUtc(workingDatabase), TimeSpan.Zero));
            }

            if (entries.TryGetValue(id, out var current))
            {
                entries[id] = current with
                {
                    ShiftName = SelectRecoveredShiftName(current.ShiftName, shiftName, createdAt),
                    CreatedAt = Min(current.CreatedAt, createdAt),
                    FilePath = file.FullName,
                    IsLegacy = false
                };
            }
            else
            {
                entries[id] = new SessionCatalogEntry(
                    id,
                    SelectRecoveredShiftName(null, shiftName, createdAt),
                    createdAt,
                    lastOpenedAt,
                    file.FullName);
            }
        }

        foreach (var databasePath in Directory.EnumerateFiles(_sessions, "tcm.sqlite", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directoryName = Directory.GetParent(databasePath)?.Name;
            var idText = directoryName?.Split('-').LastOrDefault();
            if (!Guid.TryParseExact(idText, "N", out var id) || entries.ContainsKey(id))
            {
                continue;
            }

            var file = new FileInfo(databasePath);
            var createdAt = new DateTimeOffset(file.CreationTimeUtc, TimeSpan.Zero);
            var shiftName = await TryReadShiftNameAsync(databasePath, cancellationToken);
            entries[id] = new SessionCatalogEntry(
                id,
                SelectRecoveredShiftName(null, shiftName, createdAt),
                createdAt,
                new DateTimeOffset(file.LastWriteTimeUtc, TimeSpan.Zero),
                file.FullName,
                true);
        }

        return entries.Values.OrderBy(item => item.CreatedAt).ToList();
    }

    private static string SelectRecoveredShiftName(string? current, string? recovered, DateTimeOffset createdAt)
    {
        if (!string.IsNullOrWhiteSpace(current) && !current.StartsWith("Recovered shift ", StringComparison.Ordinal))
        {
            return current;
        }

        return !string.IsNullOrWhiteSpace(recovered)
            ? recovered
            : $"Recovered shift {createdAt.LocalDateTime:g}";
    }

    private static async Task<string?> TryReadShiftNameAsync(string databasePath, CancellationToken cancellationToken)
    {
        if (!File.Exists(databasePath))
        {
            return null;
        }

        try
        {
            var connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false
            }.ToString();
            await using var connection = new SqliteConnection(connectionString);
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT value FROM session_settings WHERE key = 'shift_name' LIMIT 1;";
            return await command.ExecuteScalarAsync(cancellationToken) as string;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (SqliteException)
        {
            return null;
        }
    }

    private static async Task<bool> IsHealthySqliteDatabaseAsync(string databasePath, CancellationToken cancellationToken)
    {
        try
        {
            var connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false
            }.ToString();
            await using var connection = new SqliteConnection(connectionString);
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA quick_check;";
            return string.Equals(await command.ExecuteScalarAsync(cancellationToken) as string, "ok", StringComparison.Ordinal);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (SqliteException)
        {
            return false;
        }
    }

    private static async Task<List<SessionCatalogEntry>?> TryReadCatalogAsync(
        string path,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path) || new FileInfo(path).Length == 0)
        {
            return null;
        }

        try
        {
            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                81920,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            return await JsonSerializer.DeserializeAsync<List<SessionCatalogEntry>>(stream, cancellationToken: cancellationToken) ?? [];
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (JsonException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private async Task WriteCatalogAsync(List<SessionCatalogEntry> entries, CancellationToken cancellationToken)
    {
        var temporary = _catalogPath + $".{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var stream = new FileStream(
                temporary,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                81920,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, entries, cancellationToken: cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(true);
            }

            if (File.Exists(_catalogPath))
            {
                File.Replace(temporary, _catalogPath, _catalogBackupPath, true);
            }
            else
            {
                File.Move(temporary, _catalogPath);
            }
        }
        finally
        {
            TryDeleteFile(temporary);
        }
    }

    private static bool CatalogsMatch(
        IReadOnlyCollection<SessionCatalogEntry> first,
        IReadOnlyCollection<SessionCatalogEntry> second)
    {
        if (first.Count != second.Count)
        {
            return false;
        }

        var orderedFirst = first.OrderBy(item => item.Id);
        var orderedSecond = second.OrderBy(item => item.Id);
        return orderedFirst.SequenceEqual(orderedSecond);
    }

    private static async Task EncryptAsync(
        string source,
        string destination,
        string password,
        CancellationToken cancellationToken)
    {
        var plain = await File.ReadAllBytesAsync(source, cancellationToken);
        var salt = RandomNumberGenerator.GetBytes(SaltLength);
        var nonce = RandomNumberGenerator.GetBytes(NonceLength);
        var cipher = new byte[plain.Length];
        var tag = new byte[TagLength];
        var key = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, KeyLength);
        try
        {
            using var aes = new AesGcm(key, TagLength);
            aes.Encrypt(nonce, plain, cipher, tag);
            await File.WriteAllBytesAsync(destination, [.. "TCM1"u8, .. salt, .. nonce, .. tag, .. cipher], cancellationToken);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(plain);
        }
    }

    private static async Task DecryptAsync(
        string source,
        string destination,
        string password,
        CancellationToken cancellationToken)
    {
        var data = await File.ReadAllBytesAsync(source, cancellationToken);
        if (data.Length < 4 + SaltLength + NonceLength + TagLength || !data.AsSpan(0, 4).SequenceEqual("TCM1"u8))
        {
            throw new InvalidOperationException("This session file is invalid.");
        }

        var salt = data.AsSpan(4, SaltLength);
        var nonce = data.AsSpan(4 + SaltLength, NonceLength);
        var tag = data.AsSpan(4 + SaltLength + NonceLength, TagLength);
        var cipher = data.AsSpan(4 + SaltLength + NonceLength + TagLength);
        var plain = new byte[cipher.Length];
        var key = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, KeyLength);
        try
        {
            using var aes = new AesGcm(key, TagLength);
            aes.Decrypt(nonce, cipher, tag, plain);
            await File.WriteAllBytesAsync(destination, plain, cancellationToken);
        }
        catch (CryptographicException)
        {
            throw new InvalidOperationException("The session password is incorrect.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(plain);
        }
    }

    private static async Task DeleteWorkspaceAsync(string directory, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                if (Directory.Exists(directory))
                {
                    Directory.Delete(directory, true);
                }

                return;
            }
            catch (IOException) when (attempt < 2)
            {
                SqliteConnection.ClearAllPools();
                await Task.Delay(150, cancellationToken);
            }
        }
    }

    private bool IsWorkingDirectory(string directory)
    {
        var workingRoot = Path.GetFullPath(_working).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var candidate = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return candidate.StartsWith(workingRoot, StringComparison.OrdinalIgnoreCase);
    }

    private static DateTimeOffset Max(DateTimeOffset first, DateTimeOffset second) => first >= second ? first : second;
    private static DateTimeOffset Min(DateTimeOffset first, DateTimeOffset second) => first <= second ? first : second;

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
    }

    private string GetFilePath(Guid id) => Path.Combine(_sessions, $"{id:N}.tcm");

    private static void ValidatePassword(string password)
    {
        if (password.Length < 8)
        {
            throw new InvalidOperationException("Session passwords must have at least eight characters.");
        }
    }
}
