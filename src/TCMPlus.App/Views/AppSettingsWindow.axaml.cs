using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Interactivity;
using TCMPlus.Domain.Models;

namespace TCMPlus.App.Views;

public partial class AppSettingsWindow : Window
{
    private readonly ObservableCollection<string> _routes;
    public AppSettingsWindow(IEnumerable<string> routes, ExternalDisplayMode displayMode = ExternalDisplayMode.Dashboard)
    {
        InitializeComponent();
        _routes = new ObservableCollection<string>(routes);
        RoutesList.ItemsSource = _routes;
        DisplayModeInput.SelectedIndex = (int)displayMode;
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
    private void OnSave(object? sender, RoutedEventArgs e) => Close(new AppSettingsDraft(_routes.ToList(), (ExternalDisplayMode)Math.Clamp(DisplayModeInput.SelectedIndex, 0, 1)));
    private void OnOpenDisplay(object? sender, RoutedEventArgs e) => Close(new AppSettingsDraft(_routes.ToList(), (ExternalDisplayMode)Math.Clamp(DisplayModeInput.SelectedIndex, 0, 1), true));
}

public sealed record AppSettingsDraft(IReadOnlyList<string> DischargeRoutes, ExternalDisplayMode DisplayMode, bool OpenExternalDisplay = false);
