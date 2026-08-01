using Avalonia.Controls;
using Avalonia.Interactivity;

namespace TCMPlus.App.Views;

public partial class ConfirmPatientSwapDialog : ResponsiveDialogWindow
{
    public ConfirmPatientSwapDialog() : this(string.Empty, string.Empty, string.Empty, string.Empty)
    {
    }

    public ConfirmPatientSwapDialog(string sourcePatient, string sourceLocation, string destinationPatient, string destinationLocation)
    {
        InitializeComponent();
        DescriptionText.Text = $"{sourcePatient} is at {sourceLocation}. {destinationPatient} is at {destinationLocation}. Swap their assignments?";
    }

    private void OnConfirm(object? sender, RoutedEventArgs e) => Close(true);
    private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);
}
