using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using TCMPlus.Domain.Models;

namespace TCMPlus.App.Views;

public partial class DischargePatientDialog : ResponsiveDialogWindow
{
    public DischargePatientDialog(string station, IEnumerable<string> routes, IEnumerable<string> outcomes)
    {
        InitializeComponent();
        StationText.Text = station;
        RouteInput.ItemsSource = routes.ToList();
        RouteInput.SelectedIndex = 0;
        OutcomeInput.ItemsSource = new[] { string.Empty }.Concat(outcomes).ToList();
        OutcomeInput.SelectedIndex = 0;
    }

    public DischargePatientDialog() : this("", [], DischargeOutcomeOptions.Defaults)
    {
    }

    private void OnDischarge(object? sender, RoutedEventArgs e)
    {
        if (RouteInput.SelectedItem is string route)
        {
            var outcome = OutcomeInput.SelectedItem as string;
            Close(new DischargePatientDraft(route, string.IsNullOrWhiteSpace(outcome) ? null : outcome));
        }
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            OnDischarge(sender, e);
            e.Handled = true;
        }
    }
}

public sealed record DischargePatientDraft(string Route, string? Outcome);
