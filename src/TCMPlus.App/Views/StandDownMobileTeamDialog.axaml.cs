using Avalonia.Controls;
using Avalonia.Interactivity;
using TCMPlus.App.ViewModels;

namespace TCMPlus.App.Views;

public partial class StandDownMobileTeamDialog : ResponsiveDialogWindow
{
    public StandDownMobileTeamDialog() : this("", [])
    {
    }

    public StandDownMobileTeamDialog(string callsign, IReadOnlyList<StationViewModel> availableStations)
    {
        InitializeComponent();
        Heading.Text = $"Stand down {callsign}";
        StationInput.ItemsSource = availableStations;
        StationInput.SelectedIndex = availableStations.Count > 0 ? 0 : -1;
        TransferButton.IsEnabled = availableStations.Count > 0;
        NoStationsMessage.IsVisible = availableStations.Count == 0;
    }

    private void OnTransfer(object? sender, RoutedEventArgs e)
    {
        if (StationInput.SelectedItem is StationViewModel station)
        {
            Close(new StandDownRequest(StandDownOutcome.Transfer, station.Id));
        }
    }

    private void OnDischarge(object? sender, RoutedEventArgs e) => Close(new StandDownRequest(StandDownOutcome.Discharge, null));
    private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);
}

public enum StandDownOutcome { Transfer, Discharge }
public sealed record StandDownRequest(StandDownOutcome Outcome, Guid? StationId);
