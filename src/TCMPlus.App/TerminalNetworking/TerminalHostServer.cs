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
    private static readonly TimeSpan DefaultPairingLifetime = TimeSpan.FromMinutes(2);
    private const string HostCodeAlphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
    private readonly TerminalSecurityStore _securityStore;
    private readonly TerminalCommandExecutor _commandExecutor;
    private readonly TerminalHostServerOptions _options;
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly ConcurrentDictionary<string, AccessSession> _accessSessions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, LoginAttemptWindow> _loginAttempts = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<Guid, PendingPairing> _pendingPairings = [];
    private readonly ConcurrentDictionary<string, PairingAttemptWindow> _pairingAttempts = new(StringComparer.Ordinal);
    private readonly Guid _hostInstanceId = Guid.NewGuid();
    private readonly string _hostCode = GenerateHostCode();
    private WebApplication? _application;
    private X509Certificate2? _certificate;
    private TerminalHostAccess? _access;
    private TerminalDiscoveryResponder? _discoveryResponder;

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
    public event EventHandler<TerminalPairingRequestInfo>? PairingRequested;

    public async Task<TerminalHostAccess> StartAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycleGate.WaitAsync(cancellationToken);
        try
        {
            if (_access is not null)
            {
                return _access;
            }

            // A previous process may have terminated before it could revoke its in-memory
            // credentials. Nothing from that process is allowed to become valid again.
            await _securityStore.RevokeAllAsync(cancellationToken);
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
                TerminalProtocol.CurrentVersion,
                _hostInstanceId,
                _hostCode);
            if (_options.EnableDiscovery)
            {
                _discoveryResponder = new TerminalDiscoveryResponder(
                    new TerminalDiscoveryAdvertisement(
                        TerminalProtocol.DiscoveryMagic,
                        TerminalProtocol.CurrentVersion,
                        _hostInstanceId,
                        _hostCode,
                        port,
                        typeof(TerminalHostServer).Assembly.GetName().Version?.ToString() ?? "unknown"),
                    _options.DiscoveryOptions);
                await _discoveryResponder.StartAsync(cancellationToken);
            }
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

    public IReadOnlyList<TerminalPairingRequestInfo> GetPendingPairings()
    {
        var now = DateTimeOffset.UtcNow;
        return _pendingPairings.Values
            .Where(pairing => pairing.Status == TerminalPairingStatus.Pending && pairing.ExpiresAt > now)
            .Select(pairing => pairing.Info)
            .OrderBy(pairing => pairing.ExpiresAt)
            .ToList();
    }

    public async Task<TerminalPairingApprovalResult> ApprovePairingAsync(
        Guid pairingId,
        string verificationCode,
        CancellationToken cancellationToken = default)
    {
        await CleanupExpiredPairingsAsync(cancellationToken);
        if (!_pendingPairings.TryGetValue(pairingId, out var pairing))
        {
            return new TerminalPairingApprovalResult(false, "This terminal request is no longer available.");
        }

        await pairing.Gate.WaitAsync(cancellationToken);
        try
        {
            if (pairing.Status != TerminalPairingStatus.Pending)
            {
                return new TerminalPairingApprovalResult(false, "This terminal request has already been handled.");
            }

            if (pairing.ExpiresAt <= DateTimeOffset.UtcNow)
            {
                pairing.Status = TerminalPairingStatus.Expired;
                await RecordPairingAuditOnceAsync(pairing, "Expired", "pairing_expired", cancellationToken);
                return new TerminalPairingApprovalResult(false, "This terminal request expired. Ask the terminal to try again.");
            }

            pairing.VerificationAttempted = true;
            var suppliedCode = verificationCode.Trim();
            var codeMatches = false;
            if (suppliedCode.Length == 6 && suppliedCode.All(char.IsAsciiDigit))
            {
                var suppliedBytes = Encoding.ASCII.GetBytes(suppliedCode);
                var expectedBytes = Encoding.ASCII.GetBytes(pairing.Secrets.VerificationCode);
                try
                {
                    codeMatches = CryptographicOperations.FixedTimeEquals(suppliedBytes, expectedBytes);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(suppliedBytes);
                    CryptographicOperations.ZeroMemory(expectedBytes);
                }
            }

            if (!codeMatches)
            {
                pairing.Status = TerminalPairingStatus.Denied;
                pairing.ErrorCode = "verification_failed";
                pairing.Message = "The verification code did not match. Start a new terminal request.";
                await RecordPairingAuditOnceAsync(pairing, "Rejected", "verification_failed", cancellationToken);
                return new TerminalPairingApprovalResult(false, pairing.Message);
            }

            await _securityStore.RevokeByNameAsync(pairing.Info.TerminalName, cancellationToken);
            foreach (var session in _accessSessions
                         .Where(item => string.Equals(
                             item.Value.Registration.Name,
                             pairing.Info.TerminalName,
                             StringComparison.OrdinalIgnoreCase))
                         .ToList())
            {
                _accessSessions.TryRemove(session.Key, out _);
            }

            var credential = await _securityStore.CreateAsync(
                pairing.Info.TerminalName,
                DateTimeOffset.UtcNow.AddHours(24),
                TerminalProtocol.CurrentVersion,
                cancellationToken);
            var encrypted = pairing.Secrets.Encrypt(new TerminalPairingBootstrapCredential(
                credential.Registration.Id,
                credential.Registration.Name,
                credential.Password,
                _access!.CertificateFingerprint,
                TerminalProtocol.CurrentVersion));
            pairing.EncryptedCredential = encrypted;
            pairing.Status = TerminalPairingStatus.Approved;
            await RecordPairingAuditOnceAsync(pairing, "Approved", null, cancellationToken);
            return new TerminalPairingApprovalResult(true, $"{credential.Registration.Name} is approved for this app session.");
        }
        finally
        {
            pairing.Gate.Release();
        }
    }

    public async Task DenyPairingAsync(
        Guid pairingId,
        string reason = "Denied by the host operator.",
        CancellationToken cancellationToken = default)
    {
        await CleanupExpiredPairingsAsync(cancellationToken);
        if (!_pendingPairings.TryGetValue(pairingId, out var pairing))
        {
            return;
        }

        await pairing.Gate.WaitAsync(cancellationToken);
        try
        {
            if (pairing.Status != TerminalPairingStatus.Pending)
            {
                return;
            }

            pairing.Status = TerminalPairingStatus.Denied;
            pairing.ErrorCode = "pairing_denied";
            pairing.Message = reason;
            await RecordPairingAuditOnceAsync(pairing, "Denied", "host_denied", cancellationToken);
        }
        finally
        {
            pairing.Gate.Release();
        }
    }

    public Task<IReadOnlyList<TerminalPairingAuditEntry>> GetPairingAuditAsync(
        CancellationToken cancellationToken = default) =>
        _securityStore.GetPairingAuditAsync(cancellationToken: cancellationToken);

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycleGate.WaitAsync(cancellationToken);
        try
        {
            await _securityStore.RevokeAllAsync(cancellationToken);
            _accessSessions.Clear();
            _loginAttempts.Clear();
            _pairingAttempts.Clear();
            foreach (var pairing in _pendingPairings.Values)
            {
                if (pairing.Status == TerminalPairingStatus.Pending)
                {
                    pairing.Status = TerminalPairingStatus.Denied;
                    pairing.ErrorCode = "host_stopped";
                    pairing.Message = "The host stopped accepting terminal connections.";
                    await RecordPairingAuditOnceAsync(pairing, "Denied", "host_stopped", cancellationToken);
                }
                pairing.Dispose();
            }
            _pendingPairings.Clear();
            if (_discoveryResponder is not null)
            {
                await _discoveryResponder.DisposeAsync();
                _discoveryResponder = null;
            }
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

            if (!context.Request.Path.StartsWithSegments(TerminalProtocol.ApiRoot)
                && !context.Request.Path.StartsWithSegments(TerminalProtocol.PairingApiRoot))
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            await next(context);
        });

        application.MapPost($"{TerminalProtocol.ApiRoot}/auth/token", new Func<HttpContext, Task<IResult>>(LoginAsync));
        application.MapDelete($"{TerminalProtocol.ApiRoot}/auth/session", new Func<HttpContext, Task<IResult>>(LogoutAsync));
        application.MapGet($"{TerminalProtocol.ApiRoot}/snapshot", new Func<HttpContext, Task<IResult>>(SnapshotAsync));
        application.MapPost($"{TerminalProtocol.ApiRoot}/commands", new Func<HttpContext, Task<IResult>>(CommandAsync));
        application.MapPost($"{TerminalProtocol.PairingApiRoot}/start", new Func<HttpContext, Task<IResult>>(StartPairingAsync));
        application.MapGet(
            $"{TerminalProtocol.PairingApiRoot}/{{pairingId:guid}}",
            new Func<HttpContext, Guid, Task<IResult>>(GetPairingStatusAsync));
    }

    private async Task<IResult> StartPairingAsync(HttpContext context)
    {
        await CleanupExpiredPairingsAsync(context.RequestAborted);
        if (!HasSupportedContentType(context.Request))
        {
            return Results.Json(
                new TerminalApiError("unsupported_media_type", "Use application/json."),
                statusCode: StatusCodes.Status415UnsupportedMediaType);
        }

        TerminalPairingStartRequest? request;
        try
        {
            request = await context.Request.ReadFromJsonAsync<TerminalPairingStartRequest>(context.RequestAborted);
        }
        catch
        {
            return Results.BadRequest(new TerminalApiError("invalid_request", "The pairing request is malformed."));
        }

        if (request is null
            || request.RequestId == Guid.Empty)
        {
            return Results.BadRequest(new TerminalApiError("invalid_request", "The pairing request is invalid."));
        }

        if (request.ProtocolVersion != TerminalProtocol.CurrentVersion)
        {
            return ProtocolMismatch();
        }

        if (string.IsNullOrWhiteSpace(request.TerminalName)
            || request.TerminalName.Trim().Length is < 2 or > 48
            || request.TerminalName.Any(char.IsControl)
            || string.IsNullOrWhiteSpace(request.ClientVersion)
            || request.ClientVersion.Length is < 1 or > 64)
        {
            return Results.BadRequest(new TerminalApiError("invalid_request", "The pairing request is invalid."));
        }

        var sourceAddress = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        if (IsPairingRateLimited(sourceAddress))
        {
            return Results.Json(
                new TerminalApiError("rate_limited", "Too many terminal requests. Try again later."),
                statusCode: StatusCodes.Status429TooManyRequests);
        }

        if (_pendingPairings.Values.Count(pairing => pairing.Status == TerminalPairingStatus.Pending) >= 5)
        {
            return Results.Json(
                new TerminalApiError("pairing_busy", "The host already has several terminal requests waiting."),
                statusCode: StatusCodes.Status429TooManyRequests);
        }

        if (!TryValidatePairingEncoding(request))
        {
            return Results.BadRequest(new TerminalApiError("invalid_request", "The pairing key material is invalid."));
        }

        RecordPairingAttempt(sourceAddress);
        var pairingId = Guid.NewGuid();
        var expiresAt = DateTimeOffset.UtcNow.Add(_options.PairingLifetime ?? DefaultPairingLifetime);
        var keyExchange = new TerminalPairingKeyExchange();
        var response = new TerminalPairingStartResponse(
            pairingId,
            keyExchange.PublicKey,
            keyExchange.Nonce,
            _access!.CertificateFingerprint,
            expiresAt,
            TerminalProtocol.CurrentVersion);
        TerminalPairingSecrets secrets;
        try
        {
            secrets = keyExchange.DeriveAsHost(request, response);
        }
        catch (InvalidOperationException)
        {
            keyExchange.Dispose();
            return Results.BadRequest(new TerminalApiError("invalid_request", "The pairing key material is invalid."));
        }

        var info = new TerminalPairingRequestInfo(
            pairingId,
            request.TerminalName.Trim(),
            sourceAddress,
            request.ClientVersion,
            expiresAt);
        var pending = new PendingPairing(info, request, response, keyExchange, secrets);
        if (!_pendingPairings.TryAdd(pairingId, pending))
        {
            pending.Dispose();
            return Results.Json(
                new TerminalApiError("pairing_unavailable", "The host could not create this terminal request."),
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        await _securityStore.RecordPairingAuditAsync(
            pairingId,
            info.TerminalName,
            info.SourceAddress,
            "Created",
            cancellationToken: context.RequestAborted);
        PairingRequested?.Invoke(this, info);
        return Results.Json(response);
    }

    private async Task<IResult> GetPairingStatusAsync(HttpContext context, Guid pairingId)
    {
        await CleanupExpiredPairingsAsync(context.RequestAborted);
        if (!_pendingPairings.TryGetValue(pairingId, out var pairing))
        {
            return Results.Json(
                new TerminalApiError("pairing_not_found", "This terminal request is no longer available."),
                statusCode: StatusCodes.Status404NotFound);
        }

        var encrypted = pairing.EncryptedCredential;
        return Results.Json(new TerminalPairingStatusResponse(
            pairingId,
            pairing.Status,
            pairing.ExpiresAt,
            encrypted?.Ciphertext,
            encrypted?.Nonce,
            encrypted?.AuthenticationTag,
            pairing.ErrorCode,
            pairing.Message));
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

        if (request is null || !TerminalProtocol.IsSupported(request.ProtocolVersion))
        {
            return Results.Json(
                new TerminalApiError(
                    "protocol_mismatch",
                    $"Supported protocol versions are {TerminalProtocol.LegacyVersion} and {TerminalProtocol.CurrentVersion}."),
                statusCode: StatusCodes.Status426UpgradeRequired);
        }

        if (string.IsNullOrWhiteSpace(request.TerminalName)
            || string.IsNullOrWhiteSpace(request.Password)
            || request.TerminalName.Length > 48
            || request.Password.Length > 128)
        {
            return Results.BadRequest(new TerminalApiError("invalid_request", "The authentication request is invalid."));
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
            request.ProtocolVersion));
    }

    private async Task<IResult> SnapshotAsync(HttpContext context)
    {
        if (!HasSupportedProtocol(context.Request))
        {
            return ProtocolMismatch();
        }

        var terminal = Authorize(context);
        return terminal is null
            ? Results.Json(new TerminalApiError("unauthorized", "Authenticate this terminal again."), statusCode: StatusCodes.Status401Unauthorized)
            : Results.Json(await _commandExecutor.GetSnapshotAsync(context.RequestAborted));
    }

    private async Task<IResult> LogoutAsync(HttpContext context)
    {
        if (!HasSupportedProtocol(context.Request))
        {
            return ProtocolMismatch();
        }

        var terminal = Authorize(context);
        if (terminal is null)
        {
            return Results.Json(
                new TerminalApiError("unauthorized", "This terminal session is already closed."),
                statusCode: StatusCodes.Status401Unauthorized);
        }

        await _securityStore.RevokeAsync(terminal.Id, context.RequestAborted);
        foreach (var session in _accessSessions
                     .Where(item => item.Value.Registration.Id == terminal.Id)
                     .ToList())
        {
            _accessSessions.TryRemove(session.Key, out _);
        }

        return Results.NoContent();
    }

    private async Task<IResult> CommandAsync(HttpContext context)
    {
        if (!HasSupportedProtocol(context.Request))
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

    private bool IsPairingRateLimited(string sourceAddress)
    {
        if (!_pairingAttempts.TryGetValue(sourceAddress, out var window))
        {
            return false;
        }

        if (DateTimeOffset.UtcNow - window.StartedAt > TimeSpan.FromMinutes(10))
        {
            _pairingAttempts.TryRemove(sourceAddress, out _);
            return false;
        }

        return window.Attempts >= 3;
    }

    private void RecordPairingAttempt(string sourceAddress)
    {
        _pairingAttempts.AddOrUpdate(
            sourceAddress,
            _ => new PairingAttemptWindow(DateTimeOffset.UtcNow, 1),
            (_, existing) => DateTimeOffset.UtcNow - existing.StartedAt > TimeSpan.FromMinutes(10)
                ? new PairingAttemptWindow(DateTimeOffset.UtcNow, 1)
                : existing with { Attempts = existing.Attempts + 1 });
    }

    private static bool TryValidatePairingEncoding(TerminalPairingStartRequest request)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(request.ClientPublicKey)
                || string.IsNullOrWhiteSpace(request.ClientNonce))
            {
                return false;
            }

            var publicKey = Convert.FromBase64String(request.ClientPublicKey);
            var nonce = Convert.FromBase64String(request.ClientNonce);
            return publicKey.Length is >= 64 and <= 256 && nonce.Length == 32;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private async Task CleanupExpiredPairingsAsync(CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var item in _pendingPairings.ToList())
        {
            var pairing = item.Value;
            if (pairing.ExpiresAt > now && pairing.Status == TerminalPairingStatus.Pending)
            {
                continue;
            }

            var dispose = false;
            await pairing.Gate.WaitAsync(cancellationToken);
            try
            {
                if (pairing.ExpiresAt <= now && pairing.Status == TerminalPairingStatus.Pending)
                {
                    pairing.Status = TerminalPairingStatus.Expired;
                    pairing.ErrorCode = "pairing_expired";
                    pairing.Message = "The terminal request expired. Try again.";
                    await RecordPairingAuditOnceAsync(pairing, "Expired", "pairing_expired", cancellationToken);
                }

                if (now - pairing.ExpiresAt > TimeSpan.FromMinutes(1))
                {
                    dispose = _pendingPairings.TryRemove(item.Key, out _);
                }
            }
            finally
            {
                pairing.Gate.Release();
            }

            if (dispose)
            {
                pairing.Dispose();
            }
        }
    }

    private async Task RecordPairingAuditOnceAsync(
        PendingPairing pairing,
        string result,
        string? reason,
        CancellationToken cancellationToken)
    {
        if (pairing.OutcomeAudited)
        {
            return;
        }

        pairing.OutcomeAudited = true;
        await _securityStore.RecordPairingAuditAsync(
            pairing.Info.PairingId,
            pairing.Info.TerminalName,
            pairing.Info.SourceAddress,
            result,
            reason,
            cancellationToken);
    }

    private static IResult ProtocolMismatch() => Results.Json(
        new TerminalApiError(
            "protocol_mismatch",
            $"Supported protocol versions are {TerminalProtocol.LegacyVersion} and {TerminalProtocol.CurrentVersion}."),
        statusCode: StatusCodes.Status426UpgradeRequired);

    private static bool HasSupportedProtocol(HttpRequest request) =>
        int.TryParse(request.Headers[TerminalProtocol.VersionHeader], out var version)
        && TerminalProtocol.IsSupported(version);

    private static bool HasSupportedContentType(HttpRequest request) =>
        request.ContentType?.StartsWith("application/json", StringComparison.OrdinalIgnoreCase) == true;

    private static string HashToken(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    private async Task DisposeApplicationAsync()
    {
        if (_discoveryResponder is not null)
        {
            await _discoveryResponder.DisposeAsync();
            _discoveryResponder = null;
        }

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

    private static string GenerateHostCode() =>
        new(Enumerable.Range(0, 4)
            .Select(_ => HostCodeAlphabet[RandomNumberGenerator.GetInt32(HostCodeAlphabet.Length)])
            .ToArray());

    private sealed record AccessSession(TerminalRegistration Registration, DateTimeOffset ExpiresAt);
    private sealed record LoginAttemptWindow(DateTimeOffset StartedAt, int Failures);
    private sealed record PairingAttemptWindow(DateTimeOffset StartedAt, int Attempts);

    private sealed class PendingPairing(
        TerminalPairingRequestInfo info,
        TerminalPairingStartRequest request,
        TerminalPairingStartResponse response,
        TerminalPairingKeyExchange keyExchange,
        TerminalPairingSecrets secrets) : IDisposable
    {
        public TerminalPairingRequestInfo Info { get; } = info;
        public TerminalPairingStartRequest Request { get; } = request;
        public TerminalPairingStartResponse Response { get; } = response;
        public TerminalPairingKeyExchange KeyExchange { get; } = keyExchange;
        public TerminalPairingSecrets Secrets { get; } = secrets;
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public DateTimeOffset ExpiresAt => Response.ExpiresAt;
        public TerminalPairingStatus Status { get; set; } = TerminalPairingStatus.Pending;
        public bool VerificationAttempted { get; set; }
        public bool OutcomeAudited { get; set; }
        public TerminalEncryptedPairingCredential? EncryptedCredential { get; set; }
        public string? ErrorCode { get; set; }
        public string? Message { get; set; }

        public void Dispose()
        {
            Secrets.Dispose();
            KeyExchange.Dispose();
            Gate.Dispose();
        }
    }
}

public sealed record TerminalHostAccess(
    string CertificateFingerprint,
    IReadOnlyList<string> Addresses,
    int ProtocolVersion,
    Guid HostInstanceId,
    string HostCode);

public sealed record TerminalHostServerOptions(
    IPAddress BindAddress,
    int Port = 0,
    X509Certificate2? Certificate = null,
    bool EnableDiscovery = false,
    TerminalDiscoveryOptions? DiscoveryOptions = null,
    TimeSpan? PairingLifetime = null)
{
    public static TerminalHostServerOptions Lan { get; } = new(
        IPAddress.Any,
        EnableDiscovery: true);
}

public sealed record TerminalPairingApprovalResult(bool Approved, string Message);
