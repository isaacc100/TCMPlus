using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TCMPlus.Protocol;

namespace TCMPlus.Infrastructure.Networking;

public sealed class EncryptedTerminalCommandQueue : IDisposable
{
    private static readonly byte[] Magic = "TCQ3"u8.ToArray();
    private static readonly byte[] LegacyMagic = "TCQ2"u8.ToArray();
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string _path;
    private readonly byte[] _key;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private List<QueuedTerminalCommand> _commands;

    public EncryptedTerminalCommandQueue(
        Guid hostInstanceId,
        Uri host,
        string terminalName,
        string? applicationDataRoot = null)
    {
        applicationDataRoot ??= Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TCMPlus");
        var directory = Path.Combine(applicationDataRoot, "TerminalQueues");
        Directory.CreateDirectory(directory);
        var normalizedTerminalName = terminalName.Trim().ToUpperInvariant();
        var identity = SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{hostInstanceId:N}|{normalizedTerminalName}"));
        var identityText = Convert.ToHexString(identity)[..24];
        _path = Path.Combine(directory, $"{identityText}.v3.tcq");
        _key = TerminalQueueKeyStore.GetOrCreate(identityText, identity, applicationDataRoot);

        if (File.Exists(_path))
        {
            var contents = File.ReadAllBytes(_path);
            if (contents.Length < Magic.Length + 12 + 16 || !contents.AsSpan(0, Magic.Length).SequenceEqual(Magic))
            {
                throw new InvalidOperationException("The local terminal command queue is damaged.");
            }

            _commands = Decrypt(contents);
        }
        else
        {
            _commands = [];
            ImportLegacyQueue(host, normalizedTerminalName, applicationDataRoot, directory);
        }
    }

    public int PendingCount => _commands.Count(command => command.State == QueuedTerminalCommandState.Pending);
    public int RejectedCount => _commands.Count(command => command.State == QueuedTerminalCommandState.Rejected);
    public int UnresolvedCount => _commands.Count(command => command.State == QueuedTerminalCommandState.Unresolved);

    public async Task<IReadOnlyList<QueuedTerminalCommand>> GetAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return _commands.ToList();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task EnqueueAsync(TerminalCommandRequest request, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_commands.All(item => item.Command.RequestId != request.RequestId))
            {
                _commands.Add(new QueuedTerminalCommand(request, QueuedTerminalCommandState.Pending));
                await SaveAsync(cancellationToken);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task RemoveAsync(Guid requestId, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            _commands.RemoveAll(item => item.Command.RequestId == requestId);
            await SaveAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task RejectAsync(
        Guid requestId,
        long? sequence,
        string reason,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var index = _commands.FindIndex(item => item.Command.RequestId == requestId);
            if (index >= 0)
            {
                _commands[index] = _commands[index] with
                {
                    State = QueuedTerminalCommandState.Rejected,
                    HostSequence = sequence,
                    RejectionReason = reason
                };
                await SaveAsync(cancellationToken);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task AcknowledgeRejectedAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            _commands.RemoveAll(item => item.State == QueuedTerminalCommandState.Rejected);
            await SaveAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task MarkPendingUnresolvedAsync(
        string reason,
        CancellationToken cancellationToken = default)
    {
        var safeReason = NormalizeReason(reason);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var original = _commands;
            var updated = _commands
                .Select(command => command.State == QueuedTerminalCommandState.Pending
                    ? command with
                    {
                        State = QueuedTerminalCommandState.Unresolved,
                        RejectionReason = safeReason
                    }
                    : command)
                .ToList();
            if (!updated.SequenceEqual(original))
            {
                _commands = updated;
                try
                {
                    await SaveAsync(cancellationToken);
                }
                catch
                {
                    _commands = original;
                    throw;
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task AcknowledgeUnresolvedAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var original = _commands;
            var updated = _commands
                .Where(item => item.State != QueuedTerminalCommandState.Unresolved)
                .ToList();
            _commands = updated;
            try
            {
                await SaveAsync(cancellationToken);
            }
            catch
            {
                _commands = original;
                throw;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        CryptographicOperations.ZeroMemory(_key);
        _gate.Dispose();
    }

    private async Task SaveAsync(CancellationToken cancellationToken)
    {
        if (_commands.Count == 0)
        {
            if (File.Exists(_path))
            {
                File.Delete(_path);
            }
            return;
        }

        var contents = Encrypt(_commands, _key, Magic);
        var temporaryPath = _path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllBytesAsync(temporaryPath, contents, cancellationToken);
            File.Move(temporaryPath, _path, true);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(contents);
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private List<QueuedTerminalCommand> Decrypt(byte[] contents)
        => Decrypt(contents, _key, Magic);

    private void ImportLegacyQueue(
        Uri host,
        string normalizedTerminalName,
        string applicationDataRoot,
        string queueDirectory)
    {
        var legacyIdentity = SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{host.GetLeftPart(UriPartial.Authority)}|{normalizedTerminalName}"));
        var legacyIdentityText = Convert.ToHexString(legacyIdentity)[..24];
        var legacyPath = Path.Combine(queueDirectory, $"{legacyIdentityText}.v2.tcq");
        if (!File.Exists(legacyPath))
        {
            return;
        }

        var legacyKey = TerminalQueueKeyStore.OpenExisting(
            legacyIdentityText,
            legacyIdentity,
            applicationDataRoot);
        if (legacyKey is null)
        {
            throw new InvalidOperationException(
                "A legacy terminal command queue exists, but its protected key is unavailable.");
        }

        try
        {
            var legacyContents = File.ReadAllBytes(legacyPath);
            if (legacyContents.Length < LegacyMagic.Length + 12 + 16
                || !legacyContents.AsSpan(0, LegacyMagic.Length).SequenceEqual(LegacyMagic))
            {
                throw new InvalidOperationException("The legacy local terminal command queue is damaged.");
            }

            _commands = Decrypt(legacyContents, legacyKey, LegacyMagic)
                .Select(command => command with
                {
                    State = QueuedTerminalCommandState.Unresolved,
                    RejectionReason = "Imported from an earlier terminal queue whose host session could not be verified."
                })
                .ToList();

            SaveImportedQueue();
            File.Delete(legacyPath);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(legacyKey);
        }
    }

    private void SaveImportedQueue()
    {
        if (_commands.Count == 0)
        {
            return;
        }

        var contents = Encrypt(_commands, _key, Magic);
        var temporaryPath = _path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllBytes(temporaryPath, contents);
            File.Move(temporaryPath, _path, true);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(contents);
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static byte[] Encrypt(
        IReadOnlyList<QueuedTerminalCommand> commands,
        byte[] key,
        byte[] magic)
    {
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(commands, JsonOptions);
        try
        {
            var nonce = RandomNumberGenerator.GetBytes(12);
            var ciphertext = new byte[plaintext.Length];
            var tag = new byte[16];
            using (var aes = new AesGcm(key, tag.Length))
            {
                aes.Encrypt(nonce, plaintext, ciphertext, tag, magic);
            }

            var contents = new byte[magic.Length + nonce.Length + tag.Length + ciphertext.Length];
            var offset = 0;
            magic.CopyTo(contents, offset);
            offset += magic.Length;
            nonce.CopyTo(contents, offset);
            offset += nonce.Length;
            tag.CopyTo(contents, offset);
            offset += tag.Length;
            ciphertext.CopyTo(contents, offset);
            return contents;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    private static List<QueuedTerminalCommand> Decrypt(
        byte[] contents,
        byte[] key,
        byte[] magic)
    {
        var offset = magic.Length;
        var nonce = contents.AsSpan(offset, 12);
        offset += 12;
        var tag = contents.AsSpan(offset, 16);
        offset += 16;
        var ciphertext = contents.AsSpan(offset);
        var plaintext = new byte[ciphertext.Length];
        try
        {
            using var aes = new AesGcm(key, tag.Length);
            aes.Decrypt(nonce, ciphertext, tag, plaintext, magic);
            return JsonSerializer.Deserialize<List<QueuedTerminalCommand>>(plaintext, JsonOptions) ?? [];
        }
        catch (CryptographicException exception)
        {
            throw new InvalidOperationException("The protected local terminal command queue could not be opened.", exception);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    private static string NormalizeReason(string reason)
    {
        var normalized = new string(reason
            .Where(character => !char.IsControl(character) || char.IsWhiteSpace(character))
            .Select(character => char.IsWhiteSpace(character) ? ' ' : character)
            .ToArray());
        normalized = string.Join(' ', normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        return string.IsNullOrWhiteSpace(normalized)
            ? "This queued command cannot be safely replayed."
            : normalized[..Math.Min(normalized.Length, 240)];
    }
}

internal static class TerminalQueueKeyStore
{
    private static readonly byte[] Magic = "TQK1"u8.ToArray();

    public static byte[] GetOrCreate(
        string identityText,
        byte[] entropy,
        string applicationDataRoot)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Secure terminal queues require Windows data protection.");
        }

        var directory = Path.Combine(applicationDataRoot, "TerminalQueueKeys");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"{identityText}.key");
        if (File.Exists(path))
        {
            return Unprotect(File.ReadAllBytes(path), entropy);
        }

        var key = RandomNumberGenerator.GetBytes(32);
        var protectedKey = ProtectedData.Protect(key, entropy, DataProtectionScope.CurrentUser);
        var contents = new byte[Magic.Length + protectedKey.Length];
        Magic.CopyTo(contents, 0);
        protectedKey.CopyTo(contents, Magic.Length);
        var temporaryPath = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllBytes(temporaryPath, contents);
            try
            {
                File.Move(temporaryPath, path);
            }
            catch (IOException) when (File.Exists(path))
            {
                CryptographicOperations.ZeroMemory(key);
                return Unprotect(File.ReadAllBytes(path), entropy);
            }
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
            CryptographicOperations.ZeroMemory(protectedKey);
            CryptographicOperations.ZeroMemory(contents);
        }

        return key;
    }

    public static byte[]? OpenExisting(
        string identityText,
        byte[] entropy,
        string applicationDataRoot)
    {
        var path = Path.Combine(
            applicationDataRoot,
            "TerminalQueueKeys",
            $"{identityText}.key");
        return File.Exists(path)
            ? Unprotect(File.ReadAllBytes(path), entropy)
            : null;
    }

    private static byte[] Unprotect(byte[] contents, byte[] entropy)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Secure terminal queues require Windows data protection.");
        }

        if (contents.Length <= Magic.Length || !contents.AsSpan(0, Magic.Length).SequenceEqual(Magic))
        {
            throw new InvalidOperationException("The local terminal queue key is damaged.");
        }

        var protectedKey = contents.AsSpan(Magic.Length).ToArray();
        try
        {
            var key = ProtectedData.Unprotect(protectedKey, entropy, DataProtectionScope.CurrentUser);
            if (key.Length != 32)
            {
                CryptographicOperations.ZeroMemory(key);
                throw new InvalidOperationException("The local terminal queue key is invalid.");
            }

            return key;
        }
        catch (CryptographicException exception)
        {
            throw new InvalidOperationException(
                "The local terminal queue belongs to a different Windows user or device.",
                exception);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(protectedKey);
            CryptographicOperations.ZeroMemory(contents);
        }
    }
}

public sealed record QueuedTerminalCommand(
    TerminalCommandRequest Command,
    QueuedTerminalCommandState State,
    long? HostSequence = null,
    string? RejectionReason = null);

public enum QueuedTerminalCommandState
{
    Pending,
    Rejected,
    Unresolved
}
