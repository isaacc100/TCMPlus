using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Input;
using TCMPlus.App.ViewModels;

namespace TCMPlus.App.Views;

public partial class AddStationDialog : ResponsiveDialogWindow
{
    public AddStationDialog()
    {
        InitializeComponent();
    }

    private void OnAddStation(object? sender, RoutedEventArgs e)
    {
        var name = StationNameInput.Text?.Trim() ?? string.Empty;
        var type = StationTypeInput.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(name))
        {
            ValidationMessage.Text = "Enter a station name. Station type is optional.";
            return;
        }

        Close(new StationDraft(name, type));
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);
    private void OnNameKeyDown(object? sender, KeyEventArgs e) { if (e.Key == Key.Enter) { StationTypeInput.Focus(); e.Handled = true; } }
    private void OnSubmitKeyDown(object? sender, KeyEventArgs e) { if (e.Key == Key.Enter) { OnAddStation(sender, e); e.Handled = true; } }
}
