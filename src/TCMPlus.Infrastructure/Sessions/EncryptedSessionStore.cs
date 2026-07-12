using System.Security.Cryptography;
using System.Text.Json;
using TCMPlus.Domain.Models;

namespace TCMPlus.Infrastructure.Sessions;

public sealed class EncryptedSessionStore
{
    private const int SaltLength = 32, NonceLength = 12, TagLength = 16, KeyLength = 32, Iterations = 300_000;
    private readonly string _root, _sessions, _working, _catalogPath;

    public EncryptedSessionStore(string? root = null)
    {
        _root = root ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TCMPlus");
        _sessions = Path.Combine(_root, "Sessions"); _working = Path.Combine(_root, "Working"); _catalogPath = Path.Combine(_root, "session-catalog.json");
        Directory.CreateDirectory(_sessions); Directory.CreateDirectory(_working);
    }

    public async Task<IReadOnlyList<SessionCatalogEntry>> GetRecentAsync(CancellationToken ct = default) => (await ReadCatalogAsync(ct)).OrderByDescending(item => item.LastOpenedAt).ToList();

    public async Task<SessionDescriptor> CreateAsync(string shiftName, string password, CancellationToken ct = default)
    {
        ValidatePassword(password);
        var id = Guid.NewGuid(); var now = DateTimeOffset.UtcNow; var directory = Path.Combine(_working, id.ToString("N")); Directory.CreateDirectory(directory);
        var descriptor = new SessionDescriptor(id, now, shiftName.Trim(), directory, Path.Combine(directory, "tcm.sqlite"));
        var catalog = await ReadCatalogAsync(ct); catalog.Add(new SessionCatalogEntry(id, descriptor.ShiftName, now, now, GetFilePath(id)));
        await WriteCatalogAsync(catalog, ct); return descriptor;
    }

    public async Task<SessionDescriptor> OpenAsync(SessionCatalogEntry entry, string password, CancellationToken ct = default)
    {
        ValidatePassword(password);
        var directory = Path.Combine(_working, entry.Id.ToString("N")); Directory.CreateDirectory(directory);
        var database = Path.Combine(directory, "tcm.sqlite");
        if (entry.IsLegacy) File.Copy(entry.FilePath, database, true); else await DecryptAsync(entry.FilePath, database, password, ct);
        var catalog = await ReadCatalogAsync(ct); var index = catalog.FindIndex(item => item.Id == entry.Id); if (index >= 0) { catalog[index] = catalog[index] with { LastOpenedAt = DateTimeOffset.UtcNow }; await WriteCatalogAsync(catalog, ct); }
        return new SessionDescriptor(entry.Id, entry.CreatedAt, entry.ShiftName, directory, database);
    }

    public async Task SealAsync(SessionDescriptor session, string password, CancellationToken ct = default)
    {
        ValidatePassword(password); if (!File.Exists(session.DatabasePath)) return;
        await EncryptAsync(session.DatabasePath, GetFilePath(session.Id), password, ct);
        var catalog = await ReadCatalogAsync(ct); var index = catalog.FindIndex(item => item.Id == session.Id);
        if (index >= 0) { catalog[index] = catalog[index] with { IsLegacy = false }; await WriteCatalogAsync(catalog, ct); }
        if (Path.GetFullPath(session.DirectoryPath).StartsWith(Path.GetFullPath(_working), StringComparison.OrdinalIgnoreCase)) Directory.Delete(session.DirectoryPath, true);
    }

    public async Task RenameAsync(SessionCatalogEntry entry, string name, CancellationToken ct = default) { var catalog = await ReadCatalogAsync(ct); var index = catalog.FindIndex(item => item.Id == entry.Id); if (index >= 0) { catalog[index] = catalog[index] with { ShiftName = name.Trim() }; await WriteCatalogAsync(catalog, ct); } }
    public async Task DeleteAsync(SessionCatalogEntry entry, CancellationToken ct = default) { if (File.Exists(entry.FilePath)) File.Delete(entry.FilePath); var catalog = await ReadCatalogAsync(ct); catalog.RemoveAll(item => item.Id == entry.Id); await WriteCatalogAsync(catalog, ct); }
    public Task ExportAsync(SessionCatalogEntry entry, string destination, CancellationToken ct = default) { File.Copy(entry.FilePath, destination, true); return Task.CompletedTask; }

    private async Task EncryptAsync(string source, string destination, string password, CancellationToken ct)
    {
        var plain = await File.ReadAllBytesAsync(source, ct); var salt = RandomNumberGenerator.GetBytes(SaltLength); var nonce = RandomNumberGenerator.GetBytes(NonceLength); var cipher = new byte[plain.Length]; var tag = new byte[TagLength]; var key = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, KeyLength);
        using (var aes = new AesGcm(key, TagLength)) aes.Encrypt(nonce, plain, cipher, tag);
        await File.WriteAllBytesAsync(destination, [.. "TCM1"u8, .. salt, .. nonce, .. tag, .. cipher], ct); CryptographicOperations.ZeroMemory(key);
    }
    private async Task DecryptAsync(string source, string destination, string password, CancellationToken ct)
    {
        var data = await File.ReadAllBytesAsync(source, ct); if (data.Length < 4 + SaltLength + NonceLength + TagLength || !data.AsSpan(0,4).SequenceEqual("TCM1"u8)) throw new InvalidOperationException("This session file is invalid.");
        var salt = data.AsSpan(4,SaltLength); var nonce = data.AsSpan(4+SaltLength,NonceLength); var tag = data.AsSpan(4+SaltLength+NonceLength,TagLength); var cipher = data.AsSpan(4+SaltLength+NonceLength+TagLength); var plain = new byte[cipher.Length]; var key = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, KeyLength);
        try { using var aes = new AesGcm(key, TagLength); aes.Decrypt(nonce, cipher, tag, plain); } catch (CryptographicException) { throw new InvalidOperationException("The session password is incorrect."); } finally { CryptographicOperations.ZeroMemory(key); }
        await File.WriteAllBytesAsync(destination, plain, ct);
    }
    private async Task<List<SessionCatalogEntry>> ReadCatalogAsync(CancellationToken ct) { if (!File.Exists(_catalogPath)) return []; await using var stream = File.OpenRead(_catalogPath); return await JsonSerializer.DeserializeAsync<List<SessionCatalogEntry>>(stream, cancellationToken: ct) ?? []; }
    private async Task WriteCatalogAsync(List<SessionCatalogEntry> entries, CancellationToken ct) { await using var stream = File.Create(_catalogPath); await JsonSerializer.SerializeAsync(stream, entries, cancellationToken: ct); }
    private string GetFilePath(Guid id) => Path.Combine(_sessions, $"{id:N}.tcm");
    private static void ValidatePassword(string password) { if (password.Length < 8) throw new InvalidOperationException("Session passwords must have at least eight characters."); }
}
