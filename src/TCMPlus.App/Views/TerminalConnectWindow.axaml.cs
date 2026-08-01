using Avalonia.Interactivity;

namespace TCMPlus.App.Views;

public partial class TerminalConnectWindow : ResponsiveDialogWindow
{
    public TerminalConnectWindow()
    {
        InitializeComponent();
        ConnectView.ConnectionRequested += (_, draft) => ConnectionRequested?.Invoke(this, draft);
        Opened += async (_, _) => await ConnectView.ActivateAsync();
        Closed += (_, _) => ConnectView.Deactivate();
    }

    public event EventHandler<TerminalConnectionDraft>? ConnectionRequested;
    public void ShowError(string message) => ConnectView.ShowError(message);
    private void OnCancel(object? sender, RoutedEventArgs e) => Close();
}

public sealed record TerminalConnectionDraft(
    Guid HostInstanceId,
    Uri Host,
    string TerminalName,
    string Password,
    string CertificateFingerprint);
