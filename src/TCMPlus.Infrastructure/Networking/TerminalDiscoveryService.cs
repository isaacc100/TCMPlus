using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using TCMPlus.Protocol;

namespace TCMPlus.Infrastructure.Networking;

public sealed class TerminalDiscoveryResponder(
    TerminalDiscoveryAdvertisement advertisement,
    TerminalDiscoveryOptions? options = null) : IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly TerminalDiscoveryOptions _options = options ?? TerminalDiscoveryOptions.Default;
    private CancellationTokenSource? _cancellation;
    private UdpClient? _listener;
    private Task? _listenTask;

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_listener is not null)
        {
            return Task.CompletedTask;
        }

        cancellationToken.ThrowIfCancellationRequested();
        var listener = new UdpClient(AddressFamily.InterNetwork);
        listener.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        listener.Client.Bind(new IPEndPoint(IPAddress.Any, _options.Port));
        if (_options.JoinMulticast)
        {
            listener.JoinMulticastGroup(_options.MulticastAddress);
        }

        _cancellation = new CancellationTokenSource();
        _listener = listener;
        _listenTask = ListenAsync(listener, _cancellation.Token);
        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        var listener = _listener;
        var cancellation = _cancellation;
        var listenTask = _listenTask;
        _listener = null;
        _cancellation = null;
        _listenTask = null;
        if (listener is null)
        {
            return;
        }

        await cancellation!.CancelAsync();
        listener.Dispose();
        if (listenTask is not null)
        {
            try
            {
                await listenTask;
            }
            catch (OperationCanceledException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
        }
        cancellation.Dispose();
    }

    public ValueTask DisposeAsync() => new(StopAsync());

    private async Task ListenAsync(UdpClient listener, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            UdpReceiveResult received;
            try
            {
                received = await listener.ReceiveAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }

            if (!TerminalNetworkAddress.IsPrivateOrLocal(received.RemoteEndPoint.Address))
            {
                continue;
            }

            TerminalDiscoveryQuery? query;
            try
            {
                query = JsonSerializer.Deserialize<TerminalDiscoveryQuery>(received.Buffer, JsonOptions);
            }
            catch (JsonException)
            {
                continue;
            }

            if (query is null
                || !string.Equals(query.Magic, TerminalProtocol.DiscoveryMagic, StringComparison.Ordinal)
                || query.ProtocolVersion != TerminalProtocol.CurrentVersion
                || query.HostCode is { Length: > 0 } requestedCode
                && !string.Equals(requestedCode, advertisement.HostCode, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var payload = JsonSerializer.SerializeToUtf8Bytes(advertisement, JsonOptions);
            try
            {
                await listener.SendAsync(payload, received.RemoteEndPoint, cancellationToken);
            }
            catch (SocketException)
            {
            }
        }
    }
}

public static class TerminalDiscoveryClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static async Task<IReadOnlyList<TerminalDiscoveredHost>> DiscoverAsync(
        string? hostCode = null,
        TimeSpan? timeout = null,
        TerminalDiscoveryOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= TerminalDiscoveryOptions.Default;
        using var client = CreateClient();
        var query = new TerminalDiscoveryQuery(
            TerminalProtocol.DiscoveryMagic,
            TerminalProtocol.CurrentVersion,
            NormalizeHostCode(hostCode));
        var payload = JsonSerializer.SerializeToUtf8Bytes(query, JsonOptions);
        await client.SendAsync(payload, new IPEndPoint(options.MulticastAddress, options.Port), cancellationToken);
        if (options.SendBroadcast)
        {
            try
            {
                await client.SendAsync(payload, new IPEndPoint(IPAddress.Broadcast, options.Port), cancellationToken);
            }
            catch (SocketException)
            {
            }
        }

        return await ReceiveAsync(client, timeout ?? TimeSpan.FromSeconds(2), cancellationToken);
    }

    public static async Task<IReadOnlyList<TerminalDiscoveredHost>> ResolveAsync(
        string identifier,
        TimeSpan? timeout = null,
        TerminalDiscoveryOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= TerminalDiscoveryOptions.Default;
        var value = identifier.Trim();
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException("Enter the host code, IP address, or computer name.");
        }

        if (Uri.TryCreate(
                value.Contains("://", StringComparison.Ordinal) ? value : $"https://{value}",
                UriKind.Absolute,
                out var direct)
            && direct.Port > 0
            && value.Contains(':', StringComparison.Ordinal))
        {
            return
            [
                new TerminalDiscoveredHost(
                    Guid.Empty,
                    "Manual",
                    new Uri(direct.GetLeftPart(UriPartial.Authority)),
                    direct.Host,
                    TerminalProtocol.CurrentVersion,
                    "Unknown")
            ];
        }

        var possibleCode = NormalizeHostCode(value);
        if (possibleCode is { Length: 4 } && possibleCode.All(character => char.IsAsciiLetterOrDigit(character)))
        {
            var byCode = await DiscoverAsync(possibleCode, timeout, options, cancellationToken);
            if (byCode.Count > 0)
            {
                return byCode;
            }
        }

        IPAddress[] addresses;
        try
        {
            addresses = await Dns.GetHostAddressesAsync(value, cancellationToken);
        }
        catch (SocketException exception)
        {
            throw new InvalidOperationException("That host name could not be found on this network.", exception);
        }

        var targets = addresses
            .Where(address => address.AddressFamily == AddressFamily.InterNetwork && TerminalNetworkAddress.IsPrivateOrLocal(address))
            .Distinct()
            .ToList();
        if (targets.Count == 0)
        {
            throw new InvalidOperationException("That address is not a private LAN address.");
        }

        using var client = CreateClient();
        var query = JsonSerializer.SerializeToUtf8Bytes(
            new TerminalDiscoveryQuery(TerminalProtocol.DiscoveryMagic, TerminalProtocol.CurrentVersion),
            JsonOptions);
        foreach (var address in targets)
        {
            await client.SendAsync(query, new IPEndPoint(address, options.Port), cancellationToken);
        }

        return await ReceiveAsync(client, timeout ?? TimeSpan.FromSeconds(2), cancellationToken);
    }

    private static UdpClient CreateClient()
    {
        var client = new UdpClient(AddressFamily.InterNetwork)
        {
            EnableBroadcast = true
        };
        client.Client.Bind(new IPEndPoint(IPAddress.Any, 0));
        client.MulticastLoopback = true;
        client.Ttl = 1;
        return client;
    }

    private static async Task<IReadOnlyList<TerminalDiscoveredHost>> ReceiveAsync(
        UdpClient client,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var timeoutCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCancellation.CancelAfter(timeout);
        var hosts = new Dictionary<Guid, TerminalDiscoveredHost>();
        while (!timeoutCancellation.IsCancellationRequested)
        {
            UdpReceiveResult received;
            try
            {
                received = await client.ReceiveAsync(timeoutCancellation.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            if (!TerminalNetworkAddress.IsPrivateOrLocal(received.RemoteEndPoint.Address))
            {
                continue;
            }

            TerminalDiscoveryAdvertisement? advertisement;
            try
            {
                advertisement = JsonSerializer.Deserialize<TerminalDiscoveryAdvertisement>(received.Buffer, JsonOptions);
            }
            catch (JsonException)
            {
                continue;
            }

            if (advertisement is null
                || !string.Equals(advertisement.Magic, TerminalProtocol.DiscoveryMagic, StringComparison.Ordinal)
                || advertisement.ProtocolVersion != TerminalProtocol.CurrentVersion
                || advertisement.HttpsPort is < 1 or > 65535
                || string.IsNullOrWhiteSpace(advertisement.HostCode)
                || advertisement.HostCode.Length != 4
                || string.IsNullOrWhiteSpace(advertisement.AppVersion))
            {
                continue;
            }

            hosts[advertisement.HostInstanceId] = new TerminalDiscoveredHost(
                advertisement.HostInstanceId,
                advertisement.HostCode,
                new Uri($"https://{received.RemoteEndPoint.Address}:{advertisement.HttpsPort}"),
                received.RemoteEndPoint.Address.ToString(),
                advertisement.ProtocolVersion,
                advertisement.AppVersion);
        }

        return hosts.Values.OrderBy(host => host.HostCode, StringComparer.Ordinal).ToList();
    }

    private static string? NormalizeHostCode(string? value)
    {
        var normalized = value?.Trim().Replace("-", "", StringComparison.Ordinal).ToUpperInvariant();
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }
}

public sealed record TerminalDiscoveredHost(
    Guid HostInstanceId,
    string HostCode,
    Uri Host,
    string Address,
    int ProtocolVersion,
    string AppVersion)
{
    public string DisplayName => HostInstanceId == Guid.Empty
        ? $"{Address} (manual)"
        : $"TCM+ host {HostCode} — {Address}";

    public override string ToString() => DisplayName;
}

public sealed record TerminalDiscoveryOptions(
    int Port,
    IPAddress MulticastAddress,
    bool JoinMulticast = true,
    bool SendBroadcast = true)
{
    public static TerminalDiscoveryOptions Default { get; } = new(
        TerminalProtocol.DiscoveryPort,
        IPAddress.Parse(TerminalProtocol.DiscoveryMulticastAddress));
}

internal static class TerminalNetworkAddress
{
    public static bool IsPrivateOrLocal(IPAddress? address)
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
}
