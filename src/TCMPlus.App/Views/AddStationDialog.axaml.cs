using Avalonia.Controls;
using Avalonia.Interactivity;
using TCMPlus.App.ViewModels;

namespace TCMPlus.App.Views;

public partial class AddStationDialog : Window
{
    public AddStationDialog()
    {
        InitializeComponent();
    }

    private void OnAddStation(object? sender, RoutedEventArgs e)
    {
        var name = StationNameInput.Text?.Trim() ?? string.Empty;
        var type = StationTypeInput.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(type))
        {
            ValidationMessage.Text = "Enter both a station name and type.";
            return;
        }

        Close(new StationDraft(name, type));
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);
}
