using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace TCMPlus.App.Views;

public partial class AppSettingsWindow : Window
{
    private readonly ObservableCollection<string> _routes;
    public AppSettingsWindow(IEnumerable<string> routes)
    {
        InitializeComponent();
        _routes = new ObservableCollection<string>(routes);
        RoutesList.ItemsSource = _routes;
    }

    public AppSettingsWindow() : this([]) { }
    private void OnAddRoute(object? sender, RoutedEventArgs e)
    {
        var route = NewRouteInput.Text?.Trim();
        if (!string.IsNullOrWhiteSpace(route) && !_routes.Contains(route, StringComparer.OrdinalIgnoreCase)) _routes.Add(route);
        NewRouteInput.Text = ""; NewRouteInput.Focus();
    }
    private void OnRemoveRoute(object? sender, RoutedEventArgs e) { if (RoutesList.SelectedItem is string route && _routes.Count > 1) _routes.Remove(route); }
    private void OnCancel(object? sender, RoutedEventArgs e) => Close();
    private void OnSave(object? sender, RoutedEventArgs e) => Close(new AppSettingsDraft(_routes.ToList()));
}

public sealed record AppSettingsDraft(IReadOnlyList<string> DischargeRoutes);
