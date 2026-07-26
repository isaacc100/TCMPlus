using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TCMPlus.Infrastructure.Networking;
using TCMPlus.Protocol;

namespace TCMPlus.App.TerminalNetworking;

public sealed class TerminalHostServer : IAsyncDisposable
{
    private static readonly TimeSpan AccessTokenLifetime = TimeSpan.FromMinutes(15);
    private readonly TerminalSecurityStore _securityStore;
    private readonly TerminalCommandExecutor _commandExecutor;
    private readonly TerminalHostServerOptions _options;
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly ConcurrentDictionary<string, AccessSession> _accessSessions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, LoginAttemptWindow> _loginAttempts = new(StringComparer.Ordinal);
    private WebApplication? _application;
    private X509Certificate2? _certificate;
    private TerminalHostAccess? _access;

    public TerminalHostServer(
        TerminalSecurityStore securityStore,
        TerminalCommandExecutor commandExecutor,
        TerminalHostServerOptions? options = null)
    {
        _securityStore = securityStore;
        _commandExecutor = commandExecutor;
        _options = options ?? TerminalHostServerOptions.Lan;
    }

    public bool IsRunning => _application is not null;
    public TerminalHostAccess? Access => _access;

    public async Task<TerminalHostAccess> StartAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycleGate.WaitAsync(cancellationToken);
        try
        {
            if (_access is not null)
            {
                return _access;
            }

            _certificate = _options.Certificate ?? CreateCertificate();
            var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
            {
                ApplicationName = typeof(TerminalHostServer).Assembly.FullName,
                ContentRootPath = AppContext.BaseDirectory
            });
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(server =>
            {
                server.Limits.MaxRequestBodySize = 64 * 1024;
                server.AddServerHeader = false;
                server.Listen(_options.BindAddress, _options.Port, endpoint =>
                {
                    endpoint.Protocols = HttpProtocols.Http1AndHttp2;
                    endpoint.UseHttps(_certificate);
                });
            });

            var application = builder.Build();
            _application = application;
            MapEndpoints(application);
            await application.StartAsync(cancellationToken);
            var port = GetListeningPort(application);
            var fingerprint = Convert.ToHexString(SHA256.HashData(_certificate.RawData));
            _access = new TerminalHostAccess(
                FormatFingerprint(fingerprint),
                GetAddresses(port, _options.BindAddress).Select(address => $"https://{address}:{port}").ToList(),
                TerminalProtocol.CurrentVersion);
            return _access;
        }
        catch
        {
            await DisposeApplicationAsync();
            throw;
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async Task<TerminalCredential> CreateTerminalAsync(
        string name,
        CancellationToken cancellationToken = default) =>
        await _securityStore.CreateAsync(name, DateTimeOffset.UtcNow.AddHours(24), cancellationToken: cancellationToken);

    public Task<IReadOnlyList<TerminalRegistration>> GetTerminalsAsync(CancellationToken cancellationToken = default) =>
        _securityStore.GetRegistrationsAsync(cancellationToken);

    public async Task RevokeTerminalAsync(Guid terminalId, CancellationToken cancellationToken = default)
    {
        await _securityStore.RevokeAsync(terminalId, cancellationToken);
        foreach (var session in _accessSessions.Where(item => item.Value.Registration.Id == terminalId).ToList())
        {
            _accessSessions.TryRemove(session.Key, out _);
        }
    }

    public Task<IReadOnlyList<TerminalAuditEntry>> GetAuditAsync(CancellationToken cancellationToken = default) =>
        _securityStore.GetAuditAsync(cancellationToken: cancellationToken);

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycleGate.WaitAsync(cancellationToken);
        try
        {
            await _securityStore.RevokeAllAsync(cancellationToken);
            _accessSessions.Clear();
            _loginAttempts.Clear();
            await DisposeApplicationAsync();
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public ValueTask DisposeAsync() => new(StopAsync());

    private void MapEndpoints(WebApplication application)
    {
        application.Use(async (context, next) =>
        {
            context.Response.Headers.XContentTypeOptions = "nosniff";
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers["Referrer-Policy"] = "no-referrer";
            context.Response.Headers["Content-Security-Policy"] = "default-src 'none'; frame-ancestors 'none'";
            if (!IsPrivateOrLocal(context.Connection.RemoteIpAddress))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }

            if (!context.Request.Path.StartsWithSegments(TerminalProtocol.ApiRoot))
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            await next(context);
        });

        application.MapPost($"{TerminalProtocol.ApiRoot}/auth/token", new Func<HttpContext, Task<IResult>>(LoginAsync));
        application.MapGet($"{TerminalProtocol.ApiRoot}/snapshot", new Func<HttpContext, Task<IResult>>(SnapshotAsync));
        application.MapPost($"{TerminalProtocol.ApiRoot}/commands", new Func<HttpContext, Task<IResult>>(CommandAsync));
    }

    private async Task<IResult> LoginAsync(HttpContext context)
    {
        if (!HasSupportedContentType(context.Request))
        {
            return Results.Json(new TerminalApiError("unsupported_media_type", "Use application/json."), statusCode: StatusCodes.Status415UnsupportedMediaType);
        }

        TerminalLoginRequest? request;
        try
        {
            request = await context.Request.ReadFromJsonAsync<TerminalLoginRequest>(context.RequestAborted);
        }
        catch
        {
            return Results.BadRequest(new TerminalApiError("invalid_request", "The authentication request is malformed."));
        }

        if (request is null || request.ProtocolVersion != TerminalProtocol.CurrentVersion)
        {
            return Results.Json(
                new TerminalApiError("protocol_mismatch", $"Protocol version {TerminalProtocol.CurrentVersion} is required."),
                statusCode: StatusCodes.Status426UpgradeRequired);
        }

        var remoteAddress = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        var attemptKey = $"{remoteAddress}|{request.TerminalName.Trim().ToUpperInvariant()}";
        if (IsRateLimited(attemptKey, 5) || IsRateLimited(remoteAddress, 20))
        {
            return Results.Json(new TerminalApiError("rate_limited", "Too many authentication attempts. Try again shortly."), statusCode: StatusCodes.Status429TooManyRequests);
        }

        var terminal = await _securityStore.VerifyAsync(request.TerminalName, request.Password, request.ProtocolVersion, context.RequestAborted);
        if (terminal is null)
        {
            RecordFailedLogin(attemptKey);
            RecordFailedLogin(remoteAddress);
            return Results.Json(new TerminalApiError("invalid_credentials", "The terminal name or password is invalid."), statusCode: StatusCodes.Status401Unauthorized);
        }

        _loginAttempts.TryRemove(attemptKey, out _);
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var expiresAt = DateTimeOffset.UtcNow.Add(AccessTokenLifetime);
        _accessSessions[HashToken(token)] = new AccessSession(terminal, expiresAt);
        var shiftName = (await _commandExecutor.GetSnapshotAsync(context.RequestAborted)).ShiftName;
        return Results.Json(new TerminalLoginResponse(
            token,
            expiresAt,
            terminal.Id,
            terminal.Name,
            shiftName,
            TerminalProtocol.CurrentVersion));
    }

    private async Task<IResult> SnapshotAsync(HttpContext context)
    {
        if (!HasCurrentProtocol(context.Request))
        {
            return ProtocolMismatch();
        }

        var terminal = Authorize(context);
        return terminal is null
            ? Results.Json(new TerminalApiError("unauthorized", "Authenticate this terminal again."), statusCode: StatusCodes.Status401Unauthorized)
            : Results.Json(await _commandExecutor.GetSnapshotAsync(context.RequestAborted));
    }

    private async Task<IResult> CommandAsync(HttpContext context)
    {
        if (!HasCurrentProtocol(context.Request))
        {
            return ProtocolMismatch();
        }

        var terminal = Authorize(context);
        if (terminal is null)
        {
            return Results.Json(new TerminalApiError("unauthorized", "Authenticate this terminal again."), statusCode: StatusCodes.Status401Unauthorized);
        }

        if (!HasSupportedContentType(context.Request))
        {
            return Results.Json(new TerminalApiError("unsupported_media_type", "Use application/json."), statusCode: StatusCodes.Status415UnsupportedMediaType);
        }

        TerminalCommandRequest? request;
        try
        {
            request = await context.Request.ReadFromJsonAsync<TerminalCommandRequest>(context.RequestAborted);
        }
        catch
        {
            return Results.BadRequest(new TerminalApiError("invalid_request", "The command request is malformed."));
        }

        if (request is null)
        {
            return Results.BadRequest(new TerminalApiError("invalid_request", "A command body is required."));
        }

        return Results.Json(await _commandExecutor.ExecuteAsync(terminal, request, context.RequestAborted));
    }

    private TerminalRegistration? Authorize(HttpContext context)
    {
        var authorization = context.Request.Headers.Authorization.ToString();
        if (!authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var token = authorization["Bearer ".Length..].Trim();
        if (token.Length is < 32 or > 256)
        {
            return null;
        }

        var tokenHash = HashToken(token);
        if (!_accessSessions.TryGetValue(tokenHash, out var session))
        {
            return null;
        }

        if (session.ExpiresAt <= DateTimeOffset.UtcNow || !session.Registration.IsActive)
        {
            _accessSessions.TryRemove(tokenHash, out _);
            return null;
        }

        return session.Registration;
    }

    private bool IsRateLimited(string key, int maximumFailures)
    {
        if (!_loginAttempts.TryGetValue(key, out var window))
        {
            return false;
        }

        if (DateTimeOffset.UtcNow - window.StartedAt > TimeSpan.FromMinutes(1))
        {
            _loginAttempts.TryRemove(key, out _);
            return false;
        }

        return window.Failures >= maximumFailures;
    }

    private void RecordFailedLogin(string key)
    {
        _loginAttempts.AddOrUpdate(
            key,
            _ => new LoginAttemptWindow(DateTimeOffset.UtcNow, 1),
            (_, existing) => DateTimeOffset.UtcNow - existing.StartedAt > TimeSpan.FromMinutes(1)
                ? new LoginAttemptWindow(DateTimeOffset.UtcNow, 1)
                : existing with { Failures = existing.Failures + 1 });
    }

    private static IResult ProtocolMismatch() => Results.Json(
        new TerminalApiError("protocol_mismatch", $"Protocol version {TerminalProtocol.CurrentVersion} is required."),
        statusCode: StatusCodes.Status426UpgradeRequired);

    private static bool HasCurrentProtocol(HttpRequest request) =>
        int.TryParse(request.Headers[TerminalProtocol.VersionHeader], out var version)
        && version == TerminalProtocol.CurrentVersion;

    private static bool HasSupportedContentType(HttpRequest request) =>
        request.ContentType?.StartsWith("application/json", StringComparison.OrdinalIgnoreCase) == true;

    private static string HashToken(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    private async Task DisposeApplicationAsync()
    {
        if (_application is not null)
        {
            try
            {
                await _application.StopAsync(CancellationToken.None);
            }
            finally
            {
                await _application.DisposeAsync();
            }
        }

        _application = null;
        _access = null;
        if (_options.Certificate is null)
        {
            _certificate?.Dispose();
        }
        _certificate = null;
    }

    private static int GetListeningPort(WebApplication application)
    {
        var addresses = application.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()?.Addresses;
        var address = addresses?.FirstOrDefault() ?? throw new InvalidOperationException("The terminal host did not publish an address.");
        return new Uri(address).Port;
    }

    private static IReadOnlyList<string> GetAddresses(int port, IPAddress bindAddress)
    {
        _ = port;
        if (!bindAddress.Equals(IPAddress.Any))
        {
            return [bindAddress.Equals(IPAddress.Loopback) ? "127.0.0.1" : bindAddress.ToString()];
        }

        var addresses = new HashSet<string>(StringComparer.Ordinal);
        foreach (var network in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (network.OperationalStatus != OperationalStatus.Up
                || network.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
            {
                continue;
            }

            foreach (var unicast in network.GetIPProperties().UnicastAddresses)
            {
                var address = unicast.Address;
                if (address.AddressFamily == AddressFamily.InterNetwork
                    && !IPAddress.IsLoopback(address)
                    && IsPrivateOrLocal(address)
                    && !address.ToString().StartsWith("169.254.", StringComparison.Ordinal))
                {
                    addresses.Add(address.ToString());
                }
            }
        }

        if (addresses.Count == 0)
        {
            throw new InvalidOperationException("No active IPv4 LAN address is available.");
        }

        return addresses.Order(StringComparer.Ordinal).ToList();
    }

    internal static bool IsPrivateOrLocal(IPAddress? address)
    {
        if (address is null || IPAddress.IsLoopback(address))
        {
            return true;
        }

        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        var bytes = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            return bytes[0] == 10
                || bytes[0] == 172 && bytes[1] is >= 16 and <= 31
                || bytes[0] == 192 && bytes[1] == 168;
        }

        return address.AddressFamily == AddressFamily.InterNetworkV6
            && (address.IsIPv6LinkLocal || (bytes[0] & 0xFE) == 0xFC);
    }

    private static X509Certificate2 CreateCertificate()
    {
        using var rsa = RSA.Create(3072);
        var request = new CertificateRequest(
            "CN=TCMPlus Temporary Shift Host",
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment,
            true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName("localhost");
        san.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(san.Build());
        using var generated = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(2));
        return X509CertificateLoader.LoadPkcs12(generated.Export(X509ContentType.Pkcs12), null);
    }

    private static string FormatFingerprint(string fingerprint) =>
        string.Join(" ", Enumerable.Range(0, fingerprint.Length / 4).Select(index => fingerprint.Substring(index * 4, 4)));

    private sealed record AccessSession(TerminalRegistration Registration, DateTimeOffset ExpiresAt);
    private sealed record LoginAttemptWindow(DateTimeOffset StartedAt, int Failures);
}

public sealed record TerminalHostAccess(string CertificateFingerprint, IReadOnlyList<string> Addresses, int ProtocolVersion);

public sealed record TerminalHostServerOptions(IPAddress BindAddress, int Port = 0, X509Certificate2? Certificate = null)
{
    public static TerminalHostServerOptions Lan { get; } = new(IPAddress.Any);
}
