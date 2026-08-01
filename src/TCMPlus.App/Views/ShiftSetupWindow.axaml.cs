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
        RecentSessionsPage.OpenRequested += (_, request) => ExistingShiftRequested?.Invoke(this, request);
        TerminalConnectPage.ConnectionRequested += (_, draft) => TerminalConnectionRequested?.Invoke(this, draft);
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
    public event EventHandler<SessionOpenRequest>? ExistingShiftRequested;
    public event EventHandler<TerminalConnectionDraft>? TerminalConnectionRequested;
    public event EventHandler? UpdateCheckRequested;
    public bool IsOpeningSession { get; private set; }
    public void ShowError(string message)
    {
        IsOpeningSession = false;
        ValidationMessage.Text = message;
    }
    public void SetUpdateStatus(string message) => UpdateStatus.Text = message;
    public void ShowRecentSessionError(string message) => RecentSessionsPage.ShowError(message);
    public void ShowTerminalError(string message) => TerminalConnectPage.ShowError(message);

    public async Task ShowTerminalPageAsync()
    {
        SetPage(StartupPage.Terminal);
        await TerminalConnectPage.ActivateAsync();
    }

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

    private void OnShowStartShift(object? sender, RoutedEventArgs e) => SetPage(StartupPage.Create);
    private async void OnShowSavedShifts(object? sender, RoutedEventArgs e)
    {
        SetPage(StartupPage.Saved);
        await RecentSessionsPage.ActivateAsync();
    }
    private async void OnShowTerminal(object? sender, RoutedEventArgs e) => await ShowTerminalPageAsync();
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

    private void SetPage(StartupPage page)
    {
        if (page != StartupPage.Terminal) TerminalConnectPage.Deactivate();
        CreateShiftPage.IsVisible = page == StartupPage.Create;
        RecentSessionsPage.IsVisible = page == StartupPage.Saved;
        TerminalConnectPage.IsVisible = page == StartupPage.Terminal;
        StartShiftNav.Classes.Set("active", page == StartupPage.Create);
        OpenShiftNav.Classes.Set("active", page == StartupPage.Saved);
        TerminalNav.Classes.Set("active", page == StartupPage.Terminal);
        Title = page switch
        {
            StartupPage.Saved => "Open saved shift",
            StartupPage.Terminal => "Connect terminal",
            _ => "Start TCM+"
        };
        if (page == StartupPage.Create) ShiftNameInput.Focus();
    }

    private enum StartupPage { Create, Saved, Terminal }
}

public sealed record ShiftSetupDraft(string ShiftName, string Pin, string SessionPassword, GridDensity GridDensity);
