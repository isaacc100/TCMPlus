using Avalonia.Controls;
using Avalonia.Interactivity;
using TCMPlus.Protocol;

namespace TCMPlus.App.Views;

public partial class TerminalPairingApprovalWindow : ResponsiveDialogWindow
{
    public TerminalPairingApprovalWindow(TerminalPairingRequestInfo request)
    {
        InitializeComponent();
        TerminalNameText.Text = request.TerminalName;
        RequestDetailsText.Text =
            $"Request from {request.SourceAddress} using TCM+ {request.ClientVersion}. " +
            $"This request expires at {request.ExpiresAt.ToLocalTime():t}.";
        Opened += (_, _) => CodeInput.Focus();
    }

    public TerminalPairingApprovalWindow()
        : this(new TerminalPairingRequestInfo(Guid.Empty, "", "", "", DateTimeOffset.UtcNow))
    {
    }

    private void OnApprove(object? sender, RoutedEventArgs e)
    {
        var code = new string((CodeInput.Text ?? string.Empty).Where(char.IsAsciiDigit).ToArray());
        if (code.Length != 6)
        {
            ValidationMessage.Text = "Enter all six digits shown on the terminal.";
            return;
        }

        Close(new TerminalPairingDecision(true, code));
    }

    private void OnDeny(object? sender, RoutedEventArgs e) =>
        Close(new TerminalPairingDecision(false, null));
}

public sealed record TerminalPairingDecision(bool Approved, string? VerificationCode);
