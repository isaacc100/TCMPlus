using System.Collections.ObjectModel;
using Avalonia.Interactivity;
using TCMPlus.App.ViewModels;
using TCMPlus.Domain.Models;

namespace TCMPlus.App.Views;

public partial class TransferPatientDialog : ResponsiveDialogWindow
{
    public TransferPatientDialog(IEnumerable<PatientTransferOption> options)
    {
        InitializeComponent();
        Options = new ObservableCollection<PatientTransferOption>(options);
        SelectedOption = Options.FirstOrDefault();
        DataContext = this;
        EmptyMessage.IsVisible = Options.Count == 0;
        TransferButton.IsEnabled = Options.Count > 0;
        Opened += (_, _) => DestinationBox.Focus();
    }

    public TransferPatientDialog() : this([]) { }

    public ObservableCollection<PatientTransferOption> Options { get; }
    public PatientTransferOption? SelectedOption { get; set; }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);
    private void OnContinue(object? sender, RoutedEventArgs e) => Close(SelectedOption?.Assignment);
}
