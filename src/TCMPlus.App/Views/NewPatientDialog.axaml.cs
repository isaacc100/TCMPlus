using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Input;
using TCMPlus.App.ViewModels;

namespace TCMPlus.App.Views;

public partial class NewPatientDialog : Window
{
    public NewPatientDialog() : this(string.Empty)
    {
    }

    public NewPatientDialog(string stationName)
    {
        InitializeComponent();
        StationText.Text = $"Assigning to {stationName}";
    }

    private void OnAddPatient(object? sender, RoutedEventArgs e) => Close(new NewPatientDraft(ComplaintInput.Text?.Trim()));
    private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);
    private void OnComplaintKeyDown(object? sender, KeyEventArgs e) { if (e.Key == Key.Enter) { OnAddPatient(sender, e); e.Handled = true; } }
}
