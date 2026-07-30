using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TCMPlus.Protocol;

namespace TCMPlus.Infrastructure.Networking;

public sealed class TerminalPairingKeyExchange : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly ECDiffieHellman _key = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
    private readonly byte[] _nonce = RandomNumberGenerator.GetBytes(32);

    public string PublicKey => Convert.ToBase64String(_key.ExportSubjectPublicKeyInfo());
    public string Nonce => Convert.ToBase64String(_nonce);

    public TerminalPairingSecrets DeriveAsClient(
        TerminalPairingStartRequest request,
        TerminalPairingStartResponse response) =>
        Derive(request, response, response.HostPublicKey);

    public TerminalPairingSecrets DeriveAsHost(
        TerminalPairingStartRequest request,
        TerminalPairingStartResponse response) =>
        Derive(request, response, request.ClientPublicKey);

    public void Dispose()
    {
        CryptographicOperations.ZeroMemory(_nonce);
        _key.Dispose();
    }

    private TerminalPairingSecrets Derive(
        TerminalPairingStartRequest request,
        TerminalPairingStartResponse response,
        string peerPublicKey)
    {
        byte[] encodedPeer;
        try
        {
            encodedPeer = Convert.FromBase64String(peerPublicKey);
        }
        catch (FormatException exception)
        {
            throw new InvalidOperationException("The pairing public key is malformed.", exception);
        }

        using var peer = ECDiffieHellman.Create();
        try
        {
            peer.ImportSubjectPublicKeyInfo(encodedPeer, out var consumed);
            if (consumed != encodedPeer.Length)
            {
                throw new InvalidOperationException("The pairing public key contains unexpected data.");
            }
        }
        catch (CryptographicException exception)
        {
            throw new InvalidOperationException("The pairing public key is invalid.", exception);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(encodedPeer);
        }

        var transcript = CreateTranscript(request, response);
        var transcriptHash = SHA256.HashData(transcript);
        var sharedMaterial = _key.DeriveKeyMaterial(peer.PublicKey);
        byte[] keyMaterial;
        try
        {
            keyMaterial = HKDF.DeriveKey(
                HashAlgorithmName.SHA256,
                sharedMaterial,
                64,
                transcriptHash,
                Encoding.UTF8.GetBytes("TCMPlus terminal pairing v2"));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(sharedMaterial);
            CryptographicOperations.ZeroMemory(transcript);
        }

        var encryptionKey = keyMaterial.AsSpan(0, 32).ToArray();
        var verificationKey = keyMaterial.AsSpan(32, 32).ToArray();
        CryptographicOperations.ZeroMemory(keyMaterial);
        byte[] verificationHash;
        try
        {
            verificationHash = HMACSHA256.HashData(verificationKey, transcriptHash);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(verificationKey);
        }

        var verificationValue = BinaryPrimitives.ReadUInt32BigEndian(verificationHash) % 1_000_000;
        CryptographicOperations.ZeroMemory(verificationHash);
        return new TerminalPairingSecrets(encryptionKey, verificationValue.ToString("D6"), transcriptHash);
    }

    private static byte[] CreateTranscript(
        TerminalPairingStartRequest request,
        TerminalPairingStartResponse response) =>
        JsonSerializer.SerializeToUtf8Bytes(
            new PairingTranscript(
                request.RequestId,
                response.PairingId,
                response.HostInstanceId,
                request.TerminalName,
                request.ClientVersion,
                request.ProtocolVersion,
                request.ClientPublicKey,
                request.ClientNonce,
                response.HostPublicKey,
                response.HostNonce,
                NormalizeFingerprint(response.CertificateFingerprint),
                response.ExpiresAt),
            JsonOptions);

    private static string NormalizeFingerprint(string value) =>
        new(value.Where(Uri.IsHexDigit).Select(char.ToUpperInvariant).ToArray());

    private sealed record PairingTranscript(
        Guid RequestId,
        Guid PairingId,
        Guid HostInstanceId,
        string TerminalName,
        string ClientVersion,
        int ProtocolVersion,
        string ClientPublicKey,
        string ClientNonce,
        string HostPublicKey,
        string HostNonce,
        string CertificateFingerprint,
        DateTimeOffset ExpiresAt);
}

public sealed class TerminalPairingSecrets(
    byte[] encryptionKey,
    string verificationCode,
    byte[] associatedData) : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly byte[] _encryptionKey = encryptionKey;
    private readonly byte[] _associatedData = associatedData;

    public string VerificationCode { get; } = verificationCode;

    public TerminalEncryptedPairingCredential Encrypt(TerminalPairingBootstrapCredential credential)
    {
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(credential, JsonOptions);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[16];
        try
        {
            using var aes = new AesGcm(_encryptionKey, tag.Length);
            aes.Encrypt(nonce, plaintext, ciphertext, tag, _associatedData);
            return new TerminalEncryptedPairingCredential(
                Convert.ToBase64String(ciphertext),
                Convert.ToBase64String(nonce),
                Convert.ToBase64String(tag));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    public TerminalPairingBootstrapCredential Decrypt(TerminalEncryptedPairingCredential encrypted)
    {
        byte[] ciphertext;
        byte[] nonce;
        byte[] tag;
        try
        {
            ciphertext = Convert.FromBase64String(encrypted.Ciphertext);
            nonce = Convert.FromBase64String(encrypted.Nonce);
            tag = Convert.FromBase64String(encrypted.AuthenticationTag);
        }
        catch (FormatException exception)
        {
            throw new InvalidOperationException("The approved pairing response is malformed.", exception);
        }

        if (nonce.Length != 12 || tag.Length != 16)
        {
            throw new InvalidOperationException("The approved pairing response has invalid encryption parameters.");
        }

        var plaintext = new byte[ciphertext.Length];
        try
        {
            using var aes = new AesGcm(_encryptionKey, tag.Length);
            aes.Decrypt(nonce, ciphertext, tag, plaintext, _associatedData);
            return JsonSerializer.Deserialize<TerminalPairingBootstrapCredential>(plaintext, JsonOptions)
                ?? throw new InvalidOperationException("The approved pairing response was empty.");
        }
        catch (CryptographicException exception)
        {
            throw new InvalidOperationException("The approved pairing response failed its security check.", exception);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(ciphertext);
            CryptographicOperations.ZeroMemory(nonce);
            CryptographicOperations.ZeroMemory(tag);
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    public void Dispose()
    {
        CryptographicOperations.ZeroMemory(_encryptionKey);
        CryptographicOperations.ZeroMemory(_associatedData);
    }
}

public sealed record TerminalEncryptedPairingCredential(
    string Ciphertext,
    string Nonce,
    string AuthenticationTag);
