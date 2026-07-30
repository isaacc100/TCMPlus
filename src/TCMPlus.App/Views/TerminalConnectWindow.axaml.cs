using System.Reflection;
using Avalonia.Controls;
using Avalonia.Interactivity;
using TCMPlus.Infrastructure.Networking;

namespace TCMPlus.App.Views;

public partial class TerminalConnectWindow : ResponsiveDialogWindow
{
    private readonly TerminalConnectionPreferencesStore _preferencesStore = new();
    private readonly List<TerminalDiscoveredHost> _hosts = [];
    private CancellationTokenSource? _operationCancellation;
    private TerminalPairingSession? _pairingSession;
    private TerminalConnectionPreferences _preferences = TerminalConnectionPreferences.Default;

    public TerminalConnectWindow()
    {
        InitializeComponent();
        Opened += async (_, _) => await InitializeAsync();
        Closed += (_, _) => CancelOperation();
    }

    public event EventHandler<TerminalConnectionDraft>? ConnectionRequested;

    public void ShowError(string message)
    {
        ValidationMessage.Text = message;
        PairingCodePanel.IsVisible = false;
        SetControlsEnabled(true);
    }

    private async Task InitializeAsync()
    {
        _preferences = await _preferencesStore.LoadAsync();
        TerminalNameInput.Text = _preferences.TerminalName;
        ManualHostInput.Text = _preferences.HostIdentifier;
        await RefreshHostsAsync();
    }

    private async void OnRefresh(object? sender, RoutedEventArgs e) => await RefreshHostsAsync();

    private async Task RefreshHostsAsync()
    {
        CancelOperation();
        _operationCancellation = new CancellationTokenSource();
        SetControlsEnabled(false);
        DiscoveryMessage.Text = "Searching this LAN for TCM+ hosts…";
        ValidationMessage.Text = "";
        PairingCodePanel.IsVisible = false;
        try
        {
            var discovered = await TerminalDiscoveryClient.DiscoverAsync(
                cancellationToken: _operationCancellation.Token);
            ReplaceHosts(discovered);
            DiscoveryMessage.Text = _hosts.Count == 0
                ? "No host replied. Ask the host operator to confirm terminal connections are enabled, then try its host code or IP address."
                : $"{_hosts.Count} host{(_hosts.Count == 1 ? "" : "s")} found.";
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            DiscoveryMessage.Text = $"Automatic discovery was unavailable: {exception.Message}";
        }
        finally
        {
            SetControlsEnabled(true);
        }
    }

    private async void OnFindHost(object? sender, RoutedEventArgs e)
    {
        CancelOperation();
        _operationCancellation = new CancellationTokenSource();
        SetControlsEnabled(false);
        ValidationMessage.Text = "";
        DiscoveryMessage.Text = "Looking for that host…";
        try
        {
            var discovered = await TerminalDiscoveryClient.ResolveAsync(
                ManualHostInput.Text ?? string.Empty,
                cancellationToken: _operationCancellation.Token);
            ReplaceHosts(discovered);
            DiscoveryMessage.Text = _hosts.Count == 0
                ? "No TCM+ host answered at that code or address."
                : "Host found. Request access when ready.";
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            ValidationMessage.Text = exception.Message;
        }
        finally
        {
            SetControlsEnabled(true);
        }
    }

    private async void OnConnect(object? sender, RoutedEventArgs e)
    {
        var terminalName = TerminalNameInput.Text?.Trim() ?? string.Empty;
        if (terminalName.Length is < 2 or > 48 || terminalName.Any(char.IsControl))
        {
            ShowError("Enter a terminal name containing between 2 and 48 characters.");
            return;
        }

        var host = HostList.SelectedItem as TerminalDiscoveredHost;
        if (host is null)
        {
            ShowError("Choose a discovered host or find one using its host code or address.");
            return;
        }

        CancelOperation();
        _operationCancellation = new CancellationTokenSource();
        SetControlsEnabled(false);
        ValidationMessage.Text = "Requesting approval from the host…";
        PairingCodePanel.IsVisible = false;
        try
        {
            var clientVersion = typeof(TerminalConnectWindow).Assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                ?? typeof(TerminalConnectWindow).Assembly.GetName().Version?.ToString()
                ?? "unknown";
            _pairingSession = await TerminalPairingClient.StartAsync(
                host.Host,
                terminalName,
                clientVersion,
                _operationCancellation.Token);
            PairingCodeText.Text = _pairingSession.VerificationCode;
            PairingCodePanel.IsVisible = true;
            ValidationMessage.Text = "Waiting for the host operator to enter this code and approve the terminal…";

            var result = await _pairingSession.WaitForApprovalAsync(_operationCancellation.Token);
            if (result.HostInstanceId != host.HostInstanceId)
            {
                throw new InvalidOperationException(
                    "The host identity changed during pairing. Refresh the host list and try again.");
            }

            await _preferencesStore.SaveAsync(
                new TerminalConnectionPreferences(terminalName, host.Address),
                _operationCancellation.Token);
            ValidationMessage.Text = "Approved. Connecting securely…";
            ConnectionRequested?.Invoke(
                this,
                new TerminalConnectionDraft(
                    result.HostInstanceId,
                    result.Host,
                    result.TerminalName,
                    result.Password,
                    result.CertificateFingerprint));
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            ShowError(exception.Message);
        }
        finally
        {
            _pairingSession?.Dispose();
            _pairingSession = null;
        }
    }

    private void ReplaceHosts(IEnumerable<TerminalDiscoveredHost> hosts)
    {
        _hosts.Clear();
        _hosts.AddRange(hosts);
        HostList.ItemsSource = null;
        HostList.ItemsSource = _hosts;
        var preferred = _hosts.FirstOrDefault(host =>
            string.Equals(host.Address, _preferences.HostIdentifier, StringComparison.OrdinalIgnoreCase));
        HostList.SelectedItem = preferred ?? _hosts.FirstOrDefault();
    }

    private void SetControlsEnabled(bool enabled)
    {
        RefreshButton.IsEnabled = enabled;
        FindButton.IsEnabled = enabled;
        ConnectButton.IsEnabled = enabled;
        HostList.IsEnabled = enabled;
        ManualHostInput.IsEnabled = enabled;
        TerminalNameInput.IsEnabled = enabled;
    }

    private void CancelOperation()
    {
        _operationCancellation?.Cancel();
        _operationCancellation?.Dispose();
        _operationCancellation = null;
        _pairingSession?.Dispose();
        _pairingSession = null;
    }

    private void OnCancel(object? sender, RoutedEventArgs e)
    {
        CancelOperation();
        Close();
    }
}

public sealed record TerminalConnectionDraft(
    Guid HostInstanceId,
    Uri Host,
    string TerminalName,
    string Password,
    string CertificateFingerprint);
