using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace TCMPlus.App.Views;

public partial class MobileTeamDeploymentDialog : ResponsiveDialogWindow
{
    public MobileTeamDeploymentDialog() : this("")
    {
    }

    public MobileTeamDeploymentDialog(string callsign, string? currentLocation = null, bool edit = false)
    {
        InitializeComponent();
        Heading.Text = edit ? $"Update {callsign} location" : $"Deploy {callsign}";
        ConfirmButton.Content = edit ? "Save location" : "Deploy";
        LocationInput.Text = currentLocation;
        Opened += (_, _) => LocationInput.Focus();
    }

    private void OnConfirm(object? sender, RoutedEventArgs e) => Close(LocationInput.Text?.Trim() ?? "");
    private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);
    private void OnLocationKeyDown(object? sender, KeyEventArgs e) { if (e.Key == Key.Enter) { OnConfirm(sender, e); e.Handled = true; } }
}
