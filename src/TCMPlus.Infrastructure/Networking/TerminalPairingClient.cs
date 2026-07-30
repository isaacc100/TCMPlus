using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using TCMPlus.Protocol;

namespace TCMPlus.Infrastructure.Networking;

public static class TerminalPairingClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static async Task<TerminalPairingSession> StartAsync(
        Uri host,
        string terminalName,
        string clientVersion,
        CancellationToken cancellationToken = default)
    {
        if (!string.Equals(host.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Terminal pairing requires an HTTPS host.");
        }

        var name = terminalName.Trim();
        if (name.Length is < 2 or > 48 || name.Any(char.IsControl))
        {
            throw new InvalidOperationException("Enter a terminal name containing between 2 and 48 characters.");
        }

        string? observedFingerprint = null;
        var certificateGate = new object();
        var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, certificate, _, _) =>
            {
                if (certificate is null)
                {
                    return false;
                }

                var actual = Convert.ToHexString(SHA256.HashData(certificate.GetRawCertData()));
                lock (certificateGate)
                {
                    if (observedFingerprint is null)
                    {
                        observedFingerprint = actual;
                        return true;
                    }

                    return CryptographicOperations.FixedTimeEquals(
                        Convert.FromHexString(observedFingerprint),
                        Convert.FromHexString(actual));
                }
            }
        };
        var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri(host.GetLeftPart(UriPartial.Authority)),
            Timeout = TimeSpan.FromSeconds(15)
        };
        var keyExchange = new TerminalPairingKeyExchange();
        try
        {
            var request = new TerminalPairingStartRequest(
                Guid.NewGuid(),
                name,
                clientVersion,
                TerminalProtocol.CurrentVersion,
                keyExchange.PublicKey,
                keyExchange.Nonce);
            using var response = await httpClient.PostAsJsonAsync(
                $"{TerminalProtocol.PairingApiRoot}/start",
                request,
                JsonOptions,
                cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                throw await CreateExceptionAsync(response, cancellationToken);
            }

            var start = await response.Content.ReadFromJsonAsync<TerminalPairingStartResponse>(JsonOptions, cancellationToken)
                ?? throw new InvalidOperationException("The host returned an empty pairing response.");
            if (start.PairingId == Guid.Empty
                || start.ProtocolVersion != TerminalProtocol.CurrentVersion
                || start.ExpiresAt <= DateTimeOffset.UtcNow
                || string.IsNullOrWhiteSpace(start.HostPublicKey)
                || string.IsNullOrWhiteSpace(start.HostNonce)
                || string.IsNullOrWhiteSpace(start.CertificateFingerprint))
            {
                throw new InvalidOperationException("The host returned an invalid or incompatible pairing response.");
            }

            var expectedFingerprint = NormalizeFingerprint(start.CertificateFingerprint);
            string? actualFingerprint;
            lock (certificateGate)
            {
                actualFingerprint = observedFingerprint;
            }
            if (expectedFingerprint.Length != 64
                || actualFingerprint is null
                || !CryptographicOperations.FixedTimeEquals(
                    Convert.FromHexString(expectedFingerprint),
                    Convert.FromHexString(actualFingerprint)))
            {
                throw new InvalidOperationException("The host certificate changed during pairing.");
            }

            var secrets = keyExchange.DeriveAsClient(request, start);
            return new TerminalPairingSession(httpClient, keyExchange, secrets, start, expectedFingerprint);
        }
        catch
        {
            keyExchange.Dispose();
            httpClient.Dispose();
            throw;
        }
    }

    private static async Task<Exception> CreateExceptionAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        try
        {
            var error = await response.Content.ReadFromJsonAsync<TerminalApiError>(JsonOptions, cancellationToken);
            if (error is not null)
            {
                return new TerminalPairingException(error.Code, error.Message, response.StatusCode);
            }
        }
        catch (JsonException)
        {
        }

        return new TerminalPairingException(
            "http_error",
            $"The host returned HTTP {(int)response.StatusCode}.",
            response.StatusCode);
    }

    private static string NormalizeFingerprint(string value) =>
        new(value.Where(Uri.IsHexDigit).Select(char.ToUpperInvariant).ToArray());
}

public sealed class TerminalPairingSession : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _httpClient;
    private readonly TerminalPairingKeyExchange _keyExchange;
    private readonly TerminalPairingSecrets _secrets;
    private readonly TerminalPairingStartResponse _start;
    private readonly string _certificateFingerprint;
    private bool _disposed;

    internal TerminalPairingSession(
        HttpClient httpClient,
        TerminalPairingKeyExchange keyExchange,
        TerminalPairingSecrets secrets,
        TerminalPairingStartResponse start,
        string certificateFingerprint)
    {
        _httpClient = httpClient;
        _keyExchange = keyExchange;
        _secrets = secrets;
        _start = start;
        _certificateFingerprint = certificateFingerprint;
    }

    public Guid PairingId => _start.PairingId;
    public string VerificationCode => _secrets.VerificationCode;
    public DateTimeOffset ExpiresAt => _start.ExpiresAt;
    public Uri Host => _httpClient.BaseAddress!;

    public async Task<TerminalPairingResult> WaitForApprovalAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        while (DateTimeOffset.UtcNow < _start.ExpiresAt)
        {
            using var response = await _httpClient.GetAsync(
                $"{TerminalProtocol.PairingApiRoot}/{_start.PairingId:N}",
                cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                throw await CreateExceptionAsync(response, cancellationToken);
            }

            var status = await response.Content.ReadFromJsonAsync<TerminalPairingStatusResponse>(JsonOptions, cancellationToken)
                ?? throw new InvalidOperationException("The host returned an empty pairing status.");
            switch (status.Status)
            {
                case TerminalPairingStatus.Pending:
                    await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken);
                    continue;
                case TerminalPairingStatus.Denied:
                    throw new TerminalPairingException(
                        status.ErrorCode ?? "pairing_denied",
                        status.Message ?? "The host denied this terminal request.",
                        HttpStatusCode.Forbidden);
                case TerminalPairingStatus.Expired:
                    throw new TerminalPairingException(
                        status.ErrorCode ?? "pairing_expired",
                        status.Message ?? "The terminal request expired. Try again.",
                        HttpStatusCode.Gone);
                case TerminalPairingStatus.Approved:
                    if (status.EncryptedCredential is null || status.Nonce is null || status.AuthenticationTag is null)
                    {
                        throw new InvalidOperationException("The host returned an incomplete approved pairing response.");
                    }

                    var bootstrap = _secrets.Decrypt(new TerminalEncryptedPairingCredential(
                        status.EncryptedCredential,
                        status.Nonce,
                        status.AuthenticationTag));
                    var bootstrapFingerprint = NormalizeFingerprint(bootstrap.CertificateFingerprint);
                    if (bootstrap.TerminalId == Guid.Empty
                        || bootstrap.ProtocolVersion != TerminalProtocol.CurrentVersion
                        || string.IsNullOrWhiteSpace(bootstrap.TerminalName)
                        || string.IsNullOrWhiteSpace(bootstrap.Password)
                        || bootstrapFingerprint.Length != 64
                        || !CryptographicOperations.FixedTimeEquals(
                            Convert.FromHexString(_certificateFingerprint),
                            Convert.FromHexString(bootstrapFingerprint)))
                    {
                        throw new InvalidOperationException("The approved pairing credential was invalid.");
                    }

                    return new TerminalPairingResult(
                        Host,
                        bootstrap.TerminalId,
                        bootstrap.TerminalName,
                        bootstrap.Password,
                        bootstrap.CertificateFingerprint,
                        bootstrap.ProtocolVersion);
                default:
                    throw new InvalidOperationException("The host returned an unknown pairing status.");
            }
        }

        throw new TerminalPairingException(
            "pairing_expired",
            "The terminal request expired. Try again.",
            HttpStatusCode.Gone);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _secrets.Dispose();
        _keyExchange.Dispose();
        _httpClient.Dispose();
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    private static async Task<Exception> CreateExceptionAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        try
        {
            var error = await response.Content.ReadFromJsonAsync<TerminalApiError>(JsonOptions, cancellationToken);
            if (error is not null)
            {
                return new TerminalPairingException(error.Code, error.Message, response.StatusCode);
            }
        }
        catch (JsonException)
        {
        }

        return new TerminalPairingException(
            "http_error",
            $"The host returned HTTP {(int)response.StatusCode}.",
            response.StatusCode);
    }

    private static string NormalizeFingerprint(string value) =>
        new(value.Where(Uri.IsHexDigit).Select(char.ToUpperInvariant).ToArray());
}

public sealed record TerminalPairingResult(
    Uri Host,
    Guid TerminalId,
    string TerminalName,
    string Password,
    string CertificateFingerprint,
    int ProtocolVersion);

public sealed class TerminalPairingException(
    string code,
    string message,
    HttpStatusCode statusCode) : InvalidOperationException(message)
{
    public string Code { get; } = code;
    public HttpStatusCode StatusCode { get; } = statusCode;
}
