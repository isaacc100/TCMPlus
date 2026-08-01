using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using TCMPlus.Domain.Models;

namespace TCMPlus.App.Views;

public partial class ShiftSetupWindow : ResponsiveDialogWindow
{
    public ShiftSetupWindow()
    {
        InitializeComponent();
        Opened += (_, _) =>
        {
            ShiftNameInput.Focus();
            UpdateCheckRequested?.Invoke(this, EventArgs.Empty);
        };
    }

    public event EventHandler<ShiftSetupDraft>? ShiftStarted;
    public event EventHandler? LoadExistingRequested;
    public event EventHandler? TerminalConnectionRequested;
    public event EventHandler? UpdateCheckRequested;
    public bool IsOpeningSession { get; private set; }
    public void ShowError(string message)
    {
        IsOpeningSession = false;
        ValidationMessage.Text = message;
    }
    public void SetUpdateStatus(string message) => UpdateStatus.Text = message;

    private void OnStartShift(object? sender, RoutedEventArgs e)
    {
        var shiftName = ShiftNameInput.Text?.Trim() ?? string.Empty;
        var pin = ShiftPinInput.Text?.Trim() ?? string.Empty;
        var password = SessionPasswordInput.Text ?? string.Empty;
        if (string.IsNullOrWhiteSpace(shiftName))
        {
            ValidationMessage.Text = "Enter a shift name.";
            return;
        }

        if (pin.Length != 6 || !pin.All(char.IsAsciiDigit))
        {
            ValidationMessage.Text = "Enter a six-digit PIN.";
            return;
        }
        if (password.Length < 8 || password != (ConfirmSessionPasswordInput.Text ?? string.Empty)) { ValidationMessage.Text = "Use and confirm a session password of at least eight characters."; return; }

        IsOpeningSession = true;
        ShiftStarted?.Invoke(this, new ShiftSetupDraft(shiftName, pin, password, GridDensity.Compact));
    }

    private void OnLoadExistingShift(object? sender, RoutedEventArgs e) => LoadExistingRequested?.Invoke(this, EventArgs.Empty);
    private void OnConnectTerminal(object? sender, RoutedEventArgs e) => TerminalConnectionRequested?.Invoke(this, EventArgs.Empty);
    private void OnCheckForUpdates(object? sender, RoutedEventArgs e) => UpdateCheckRequested?.Invoke(this, EventArgs.Empty);

    private void OnPinKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            SessionPasswordInput.Focus();
            e.Handled = true;
        }
    }
    private void OnTogglePinVisibility(object? sender, RoutedEventArgs e) =>
        ShiftPinInput.PasswordChar = sender is CheckBox { IsChecked: true } ? '\0' : '*';
}

public sealed record ShiftSetupDraft(string ShiftName, string Pin, string SessionPassword, GridDensity GridDensity);
