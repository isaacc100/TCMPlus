using Avalonia.Controls;
using Avalonia.Interactivity;

namespace TCMPlus.App.Views;

public partial class TerminalConnectWindow : Window
{
    public TerminalConnectWindow()
    {
        InitializeComponent();
        Opened += (_, _) => HostInput.Focus();
    }

    public event EventHandler<TerminalConnectionDraft>? ConnectionRequested;

    public void ShowError(string message)
    {
        ValidationMessage.Text = message;
        ConnectButton.IsEnabled = true;
    }

    private void OnConnect(object? sender, RoutedEventArgs e)
    {
        var hostText = HostInput.Text?.Trim() ?? string.Empty;
        var terminalName = TerminalNameInput.Text?.Trim() ?? string.Empty;
        var password = PasswordInput.Text ?? string.Empty;
        var fingerprint = FingerprintInput.Text?.Trim() ?? string.Empty;
        if (!Uri.TryCreate(hostText, UriKind.Absolute, out var host)
            || !string.Equals(host.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            ShowError("Enter the complete HTTPS address shown by the host.");
            return;
        }

        if (terminalName.Length < 2 || string.IsNullOrWhiteSpace(password) || string.IsNullOrWhiteSpace(fingerprint))
        {
            ShowError("Enter the terminal name, temporary password, and complete certificate fingerprint.");
            return;
        }

        ConnectButton.IsEnabled = false;
        ValidationMessage.Text = "Connecting securely…";
        ConnectionRequested?.Invoke(this, new TerminalConnectionDraft(host, terminalName, password, fingerprint));
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close();
}

public sealed record TerminalConnectionDraft(Uri Host, string TerminalName, string Password, string CertificateFingerprint);
