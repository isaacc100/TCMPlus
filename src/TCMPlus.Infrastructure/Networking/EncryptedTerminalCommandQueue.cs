using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TCMPlus.Protocol;

namespace TCMPlus.Infrastructure.Networking;

public sealed class EncryptedTerminalCommandQueue : IDisposable
{
    private static readonly byte[] Magic = "TCQ2"u8.ToArray();
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string _path;
    private readonly byte[] _key;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private List<QueuedTerminalCommand> _commands;

    public EncryptedTerminalCommandQueue(
        Uri host,
        string terminalName,
        string? applicationDataRoot = null)
    {
        applicationDataRoot ??= Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TCMPlus");
        var directory = Path.Combine(applicationDataRoot, "TerminalQueues");
        Directory.CreateDirectory(directory);
        var identity = SHA256.HashData(Encoding.UTF8.GetBytes($"{host.GetLeftPart(UriPartial.Authority)}|{terminalName.Trim().ToUpperInvariant()}"));
        var identityText = Convert.ToHexString(identity)[..24];
        _path = Path.Combine(directory, $"{identityText}.v2.tcq");
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
        }
    }

    public int PendingCount => _commands.Count(command => command.State == QueuedTerminalCommandState.Pending);
    public int RejectedCount => _commands.Count(command => command.State == QueuedTerminalCommandState.Rejected);

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

        var plaintext = JsonSerializer.SerializeToUtf8Bytes(_commands, JsonOptions);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[16];
        using (var aes = new AesGcm(_key, tag.Length))
        {
            aes.Encrypt(nonce, plaintext, ciphertext, tag, Magic);
        }

        CryptographicOperations.ZeroMemory(plaintext);
        var contents = new byte[Magic.Length + nonce.Length + tag.Length + ciphertext.Length];
        var offset = 0;
        Magic.CopyTo(contents, offset);
        offset += Magic.Length;
        nonce.CopyTo(contents, offset);
        offset += nonce.Length;
        tag.CopyTo(contents, offset);
        offset += tag.Length;
        ciphertext.CopyTo(contents, offset);

        var temporaryPath = _path + ".tmp";
        await File.WriteAllBytesAsync(temporaryPath, contents, cancellationToken);
        File.Move(temporaryPath, _path, true);
    }

    private List<QueuedTerminalCommand> Decrypt(byte[] contents)
    {
        var offset = Magic.Length;
        var nonce = contents.AsSpan(offset, 12);
        offset += 12;
        var tag = contents.AsSpan(offset, 16);
        offset += 16;
        var ciphertext = contents.AsSpan(offset);
        var plaintext = new byte[ciphertext.Length];
        try
        {
            using var aes = new AesGcm(_key, tag.Length);
            aes.Decrypt(nonce, ciphertext, tag, plaintext, Magic);
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

public enum QueuedTerminalCommandState { Pending, Rejected }
