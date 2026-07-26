using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using TCMPlus.Protocol;

namespace TCMPlus.Infrastructure.Networking;

public interface ITerminalApiClient : IDisposable
{
    Uri Host { get; }
    TerminalLoginResponse? Login { get; }
    Task<TerminalLoginResponse> AuthenticateAsync(CancellationToken cancellationToken = default);
    Task<TerminalSnapshotResponse> GetSnapshotAsync(CancellationToken cancellationToken = default);
    Task<TerminalCommandResponse> SendCommandAsync(TerminalCommandRequest command, CancellationToken cancellationToken = default);
}

public sealed class TerminalApiClient : ITerminalApiClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _httpClient;
    private readonly string _terminalName;
    private readonly string _password;
    private string? _accessToken;
    private DateTimeOffset _accessTokenExpiresAt;

    public TerminalApiClient(Uri host, string terminalName, string password, string certificateFingerprint)
    {
        if (!string.Equals(host.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Terminal connections require an HTTPS host address.");
        }

        var expectedFingerprint = NormalizeFingerprint(certificateFingerprint);
        if (expectedFingerprint.Length != 64 || !expectedFingerprint.All(Uri.IsHexDigit))
        {
            throw new InvalidOperationException("Enter the complete SHA-256 certificate fingerprint shown by the host.");
        }

        _terminalName = terminalName.Trim();
        _password = password;
        var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, certificate, _, policyErrors) =>
            {
                if (certificate is null)
                {
                    return false;
                }

                var actual = Convert.ToHexString(SHA256.HashData(certificate.GetRawCertData()));
                return CryptographicOperations.FixedTimeEquals(
                    Convert.FromHexString(actual),
                    Convert.FromHexString(expectedFingerprint));
            }
        };
        _httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri(host.GetLeftPart(UriPartial.Authority)),
            Timeout = TimeSpan.FromSeconds(15)
        };
        _httpClient.DefaultRequestHeaders.Add(TerminalProtocol.VersionHeader, TerminalProtocol.CurrentVersion.ToString());
    }

    public Uri Host => _httpClient.BaseAddress!;
    public TerminalLoginResponse? Login { get; private set; }

    public async Task<TerminalLoginResponse> AuthenticateAsync(CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.PostAsJsonAsync(
            $"{TerminalProtocol.ApiRoot}/auth/token",
            new TerminalLoginRequest(_terminalName, _password, TerminalProtocol.CurrentVersion),
            JsonOptions,
            cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw await CreateExceptionAsync(response, cancellationToken);
        }

        Login = await response.Content.ReadFromJsonAsync<TerminalLoginResponse>(JsonOptions, cancellationToken)
            ?? throw new InvalidOperationException("The host returned an empty authentication response.");
        _accessToken = Login.AccessToken;
        _accessTokenExpiresAt = Login.ExpiresAt;
        return Login;
    }

    public async Task<TerminalSnapshotResponse> GetSnapshotAsync(CancellationToken cancellationToken = default)
    {
        using var response = await SendAuthenticatedAsync(
            () => new HttpRequestMessage(HttpMethod.Get, $"{TerminalProtocol.ApiRoot}/snapshot"),
            cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw await CreateExceptionAsync(response, cancellationToken);
        }

        return await response.Content.ReadFromJsonAsync<TerminalSnapshotResponse>(JsonOptions, cancellationToken)
            ?? throw new InvalidOperationException("The host returned an empty terminal snapshot.");
    }

    public async Task<TerminalCommandResponse> SendCommandAsync(
        TerminalCommandRequest command,
        CancellationToken cancellationToken = default)
    {
        using var response = await SendAuthenticatedAsync(
            () =>
            {
                var request = new HttpRequestMessage(HttpMethod.Post, $"{TerminalProtocol.ApiRoot}/commands")
                {
                    Content = JsonContent.Create(command, options: JsonOptions)
                };
                return request;
            },
            cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw await CreateExceptionAsync(response, cancellationToken);
        }

        return await response.Content.ReadFromJsonAsync<TerminalCommandResponse>(JsonOptions, cancellationToken)
            ?? throw new InvalidOperationException("The host returned an empty command response.");
    }

    public void Dispose()
    {
        _accessToken = null;
        _httpClient.Dispose();
    }

    private async Task<HttpResponseMessage> SendAuthenticatedAsync(
        Func<HttpRequestMessage> requestFactory,
        CancellationToken cancellationToken)
    {
        if (_accessToken is null || _accessTokenExpiresAt <= DateTimeOffset.UtcNow.AddSeconds(15))
        {
            await AuthenticateAsync(cancellationToken);
        }

        var response = await SendOnceAsync(requestFactory(), cancellationToken);
        if (response.StatusCode != HttpStatusCode.Unauthorized)
        {
            return response;
        }

        response.Dispose();
        await AuthenticateAsync(cancellationToken);
        return await SendOnceAsync(requestFactory(), cancellationToken);
    }

    private async Task<HttpResponseMessage> SendOnceAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _accessToken);
        return await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
    }

    private static async Task<Exception> CreateExceptionAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            var error = await response.Content.ReadFromJsonAsync<TerminalApiError>(JsonOptions, cancellationToken);
            if (error is not null)
            {
                return new TerminalApiException(error.Code, error.Message, response.StatusCode);
            }
        }
        catch (JsonException)
        {
        }

        return new TerminalApiException("http_error", $"The host returned HTTP {(int)response.StatusCode}.", response.StatusCode);
    }

    private static string NormalizeFingerprint(string value) =>
        new(value.Where(Uri.IsHexDigit).Select(char.ToUpperInvariant).ToArray());
}

public sealed class TerminalApiException(string code, string message, HttpStatusCode statusCode) : InvalidOperationException(message)
{
    public string Code { get; } = code;
    public HttpStatusCode StatusCode { get; } = statusCode;
}
