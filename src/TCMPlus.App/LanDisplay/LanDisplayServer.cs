using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace TCMPlus.App.LanDisplay;

public sealed class LanDisplayServer : IAsyncDisposable
{
    private const string ViewerCookie = "tcmplus_viewer";
    private readonly LanDisplaySnapshotProvider _snapshotProvider;
    private readonly LanDisplayServerOptions _options;
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly ConcurrentDictionary<string, byte> _viewerSessions = new(StringComparer.Ordinal);
    private WebApplication? _application;
    private LanDisplayAccess? _access;
    private string? _viewerPin;

    public LanDisplayServer(LanDisplaySnapshotProvider snapshotProvider, LanDisplayServerOptions? options = null)
    {
        _snapshotProvider = snapshotProvider;
        _options = options ?? LanDisplayServerOptions.Lan;
    }

    public bool IsRunning => _application is not null;

    public async Task<LanDisplayAccess> StartAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycleGate.WaitAsync(cancellationToken);
        try
        {
            if (_access is not null) return _access;

            _viewerPin = RandomNumberGenerator.GetInt32(0, 100_000_000).ToString("D8");
            var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
            {
                ApplicationName = typeof(LanDisplayServer).Assembly.FullName,
                ContentRootPath = AppContext.BaseDirectory
            });
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(server => server.Listen(_options.BindAddress, 0, endpoint => endpoint.Protocols = HttpProtocols.Http1));

            var application = builder.Build();
            _application = application;
            MapEndpoints(application);
            await application.StartAsync(cancellationToken);

            var port = GetListeningPort(application);
            _access = new LanDisplayAccess(FormatPin(_viewerPin), GetAddresses(port, _options.BindAddress));
            return _access;
        }
        catch
        {
            if (_application is not null)
            {
                try { await _application.StopAsync(CancellationToken.None); }
                finally { await _application.DisposeAsync(); }
                _application = null;
            }
            _viewerPin = null;
            _viewerSessions.Clear();
            throw;
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycleGate.WaitAsync(cancellationToken);
        try
        {
            if (_application is not null)
            {
                await _application.StopAsync(cancellationToken);
                await _application.DisposeAsync();
            }
            _application = null;
            _access = null;
            _viewerPin = null;
            _viewerSessions.Clear();
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public ValueTask DisposeAsync() => new(StopAsync());

    private void MapEndpoints(WebApplication application)
    {
        application.MapGet("/", (HttpContext context) => IsViewer(context)
            ? Results.Redirect("/dashboard")
            : LoginPage(context, "/dashboard"));
        application.MapGet("/login", (HttpContext context) => LoginPage(context, NormalizeReturnUrl(context.Request.Query["returnUrl"])));
        application.MapPost("/login", new Func<HttpContext, Task<IResult>>(LoginAsync));
        application.MapGet("/dashboard", (HttpContext context) => ViewerPage(context, "dashboard"));
        application.MapGet("/map", (HttpContext context) => ViewerPage(context, "map"));
        application.MapGet("/api/snapshot", new Func<HttpContext, Task<IResult>>(SnapshotAsync));
        application.MapGet("/assets/display.css", (HttpContext context) => Resource(context, "display.css", "text/css; charset=utf-8"));
        application.MapGet("/assets/display.js", (HttpContext context) => Resource(context, "display.js", "text/javascript; charset=utf-8"));
    }

    private async Task<IResult> LoginAsync(HttpContext context)
    {
        var form = await context.Request.ReadFormAsync(context.RequestAborted);
        var suppliedPin = form["pin"].ToString();
        var returnUrl = NormalizeReturnUrl(form["returnUrl"].ToString());
        if (!PinMatches(suppliedPin)) return LoginPage(context, returnUrl, "Enter the eight-digit viewer PIN shown in TCM+.");

        var session = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        _viewerSessions.TryAdd(session, 0);
        context.Response.Cookies.Append(ViewerCookie, session, new CookieOptions
        {
            HttpOnly = true,
            SameSite = SameSiteMode.Strict,
            IsEssential = true,
            Path = "/"
        });
        return Results.Redirect(returnUrl);
    }

    private async Task<IResult> SnapshotAsync(HttpContext context)
    {
        if (!IsViewer(context)) return Results.Unauthorized();
        context.Response.Headers.CacheControl = "no-store";
        return Results.Json(await _snapshotProvider.GetAsync(context.RequestAborted));
    }

    private IResult ViewerPage(HttpContext context, string view)
    {
        if (!IsViewer(context)) return Results.Redirect($"/login?returnUrl=/{view}");
        context.Response.Headers.CacheControl = "no-store";
        return Results.Content(ReadResource("display.html").Replace("{{VIEW}}", view, StringComparison.Ordinal), "text/html; charset=utf-8");
    }

    private static IResult Resource(HttpContext context, string resource, string contentType)
    {
        context.Response.Headers.CacheControl = "public, max-age=300";
        return Results.Content(ReadResource(resource), contentType);
    }

    private static IResult LoginPage(HttpContext context, string returnUrl, string? error = null)
    {
        context.Response.Headers.CacheControl = "no-store";
        var html = ReadResource("login.html")
            .Replace("{{RETURN_URL}}", WebUtility.HtmlEncode(returnUrl), StringComparison.Ordinal)
            .Replace("{{ERROR}}", error is null ? string.Empty : $"<p class=\"login-error\">{WebUtility.HtmlEncode(error)}</p>", StringComparison.Ordinal);
        return Results.Content(html, "text/html; charset=utf-8");
    }

    private bool IsViewer(HttpContext context) => context.Request.Cookies.TryGetValue(ViewerCookie, out var session) && _viewerSessions.ContainsKey(session);

    private bool PinMatches(string suppliedPin)
    {
        if (_viewerPin is null || suppliedPin.Length != _viewerPin.Length) return false;
        return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(suppliedPin), Encoding.UTF8.GetBytes(_viewerPin));
    }

    private static int GetListeningPort(WebApplication application)
    {
        var addresses = application.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()?.Addresses;
        var address = addresses?.FirstOrDefault() ?? throw new InvalidOperationException("The LAN display server did not publish an address.");
        return new Uri(address).Port;
    }

    private static IReadOnlyList<LanDisplayAddress> GetAddresses(int port, IPAddress bindAddress)
    {
        var addresses = new HashSet<IPAddress>();
        if (!bindAddress.Equals(IPAddress.Any))
        {
            addresses.Add(bindAddress);
        }
        else
        {
            foreach (var network in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (network.OperationalStatus != OperationalStatus.Up || network.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
                foreach (var unicast in network.GetIPProperties().UnicastAddresses)
                {
                    if (unicast.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(unicast.Address) && !unicast.Address.ToString().StartsWith("169.254.", StringComparison.Ordinal)) addresses.Add(unicast.Address);
                }
            }
        }

        if (addresses.Count == 0) throw new InvalidOperationException("No active IPv4 LAN address is available.");
        return addresses.OrderBy(address => address.ToString(), StringComparer.Ordinal)
            .Select(address => new LanDisplayAddress(address.ToString(), $"http://{address}:{port}/dashboard", $"http://{address}:{port}/map"))
            .ToList();
    }

    private static string ReadResource(string name)
    {
        var assembly = typeof(LanDisplayServer).Assembly;
        var resourceName = assembly.GetManifestResourceNames().Single(resource => resource.EndsWith($".LanDisplay.Web.{name}", StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(resourceName) ?? throw new InvalidOperationException($"Missing LAN display resource {name}.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static string NormalizeReturnUrl(string? returnUrl) => returnUrl is "/dashboard" or "/map" ? returnUrl : "/dashboard";
    private static string FormatPin(string pin) => $"{pin[..4]} {pin[4..]}";
}

public sealed record LanDisplayAddress(string Host, string DashboardUrl, string MapUrl);
public sealed record LanDisplayAccess(string ViewerPin, IReadOnlyList<LanDisplayAddress> Addresses);
public sealed record LanDisplayServerOptions(IPAddress BindAddress)
{
    public static LanDisplayServerOptions Lan { get; } = new(IPAddress.Any);
}
