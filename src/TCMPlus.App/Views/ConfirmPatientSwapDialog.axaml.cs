using Avalonia.Controls;
using Avalonia.Interactivity;

namespace TCMPlus.App.Views;

public partial class ConfirmPatientSwapDialog : ResponsiveDialogWindow
{
    public ConfirmPatientSwapDialog() : this(string.Empty, string.Empty)
    {
    }

    public ConfirmPatientSwapDialog(string sourceStation, string destinationStation)
    {
        InitializeComponent();
        DescriptionText.Text = $"Move the patient in {sourceStation} to {destinationStation}?";
    }

    private void OnConfirm(object? sender, RoutedEventArgs e) => Close(true);
    private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);
}
