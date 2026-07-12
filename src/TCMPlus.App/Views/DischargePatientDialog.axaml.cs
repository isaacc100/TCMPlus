using Avalonia.Controls;
using Avalonia.Interactivity;

namespace TCMPlus.App.Views;
public partial class DischargePatientDialog : Window
{
    public DischargePatientDialog(string station, IEnumerable<string> routes) { InitializeComponent(); StationText.Text = station; RouteInput.ItemsSource = routes.ToList(); RouteInput.SelectedIndex = 0; }
    public DischargePatientDialog() : this("", []) { }
    private void OnDischarge(object? sender, RoutedEventArgs e) { if (RouteInput.SelectedItem is string route) Close(route); }
    private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);
}
