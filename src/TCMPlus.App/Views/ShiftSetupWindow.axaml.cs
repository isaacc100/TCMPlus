using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using System.Reflection;
using TCMPlus.Domain.Models;

namespace TCMPlus.App.Views;

public partial class ShiftSetupWindow : ResponsiveDialogWindow
{
    public ShiftSetupWindow()
    {
        InitializeComponent();
        var informationalVersion = typeof(ShiftSetupWindow).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        VersionLabel.Text = informationalVersion?.Split('+')[0] ?? "Development build";
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
        ClearValidation();
        var shiftName = ShiftNameInput.Text?.Trim() ?? string.Empty;
        var pin = ShiftPinInput.Text?.Trim() ?? string.Empty;
        var password = SessionPasswordInput.Text ?? string.Empty;
        if (string.IsNullOrWhiteSpace(shiftName))
        {
            ShiftNameError.Text = "Enter a shift name.";
            ValidationMessage.Text = "Correct the highlighted field to continue.";
            ShiftNameInput.Focus();
            return;
        }

        if (pin.Length != 6 || !pin.All(char.IsAsciiDigit))
        {
            ShiftPinError.Text = "Enter exactly six digits.";
            ValidationMessage.Text = "Correct the highlighted field to continue.";
            ShiftPinInput.Focus();
            return;
        }
        if (password.Length < 8)
        {
            PasswordError.Text = "Use at least eight characters.";
            ValidationMessage.Text = "Correct the highlighted field to continue.";
            SessionPasswordInput.Focus();
            return;
        }

        if (password != (ConfirmSessionPasswordInput.Text ?? string.Empty))
        {
            ConfirmPasswordError.Text = "Passwords do not match.";
            ValidationMessage.Text = "Correct the highlighted field to continue.";
            ConfirmSessionPasswordInput.Focus();
            return;
        }

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
    private void OnTogglePinVisibility(object? sender, RoutedEventArgs e)
    {
        var show = ShiftPinInput.PasswordChar != '\0';
        ShiftPinInput.PasswordChar = show ? '\0' : '*';
        if (sender is Button button)
        {
            button.Content = show ? "Hide" : "Show";
        }
    }

    private void ClearValidation()
    {
        ShiftNameError.Text = string.Empty;
        ShiftPinError.Text = string.Empty;
        PasswordError.Text = string.Empty;
        ConfirmPasswordError.Text = string.Empty;
        ValidationMessage.Text = string.Empty;
    }
}

public sealed record ShiftSetupDraft(string ShiftName, string Pin, string SessionPassword, GridDensity GridDensity);
