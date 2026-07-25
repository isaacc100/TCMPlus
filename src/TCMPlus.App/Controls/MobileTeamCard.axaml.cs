using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using TCMPlus.App.ViewModels;

namespace TCMPlus.App.Controls;

public partial class MobileTeamCard : UserControl
{
    public MobileTeamCard()
    {
        InitializeComponent();
    }

    private MobileTeamViewModel? ViewModel => DataContext as MobileTeamViewModel;

    private void OnCardPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Source is Visual source && (source is Button || source.GetVisualAncestors().OfType<Button>().Any()))
        {
            return;
        }

        if (ViewModel is { IsAvailable: true } viewModel && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            viewModel.DeployCommand.Execute(null);
            e.Handled = true;
        }
    }

    private async void OnPatientPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (ViewModel?.CurrentPatient is not { } patient || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        e.Handled = true;
        var data = new DataTransfer();
        data.Add(DataTransferItem.Create(PatientDragData.Format, patient.Uid.ToString("N")));
        await DragDrop.DoDragDropAsync(e, data, DragDropEffects.Move);
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        var patientUid = e.DataTransfer.TryGetValue(PatientDragData.Format);
        if (ViewModel is { CanAcceptPatientDrop: true } viewModel && patientUid is not null)
        {
            viewModel.IsDropTarget = true;
            e.DragEffects = DragDropEffects.Move;
            return;
        }

        e.DragEffects = DragDropEffects.None;
    }

    private void OnDragLeave(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is not null) ViewModel.IsDropTarget = false;
    }

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        if (ViewModel is not { CanAcceptPatientDrop: true } viewModel)
        {
            return;
        }

        viewModel.IsDropTarget = false;
        var patientUid = e.DataTransfer.TryGetValue(PatientDragData.Format);
        if (patientUid is null || !Guid.TryParse(patientUid, out var parsedPatientUid))
        {
            return;
        }

        await viewModel.DropPatientAsync(parsedPatientUid);
        e.Handled = true;
    }
}
