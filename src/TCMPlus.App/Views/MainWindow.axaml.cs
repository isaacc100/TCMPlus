using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using TCMPlus.App.Controls;
using TCMPlus.App.ViewModels;
using TCMPlus.Domain.Models;
using TCMPlus.Protocol;

namespace TCMPlus.App.Views;

public partial class MainWindow : Window
{
    private static readonly DataFormat<string> StationOrderFormat = DataFormat.CreateStringApplicationFormat("TCMPlus.StationOrder");
    private const double StationMapAspectRatio = 5d / 3d;
    private MainViewModel? _viewModel;
    private WindowState _windowStateBeforeFullScreen = WindowState.Normal;
    private ExternalDisplayWindow? _externalDisplay;
    private bool _isMobileTeamDrawerOpen;
    private readonly SemaphoreSlim _pairingApprovalGate = new(1, 1);

    public MainWindow()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        SizeChanged += (_, _) => UpdateMobileTeamLayout();
        StationMapHost.SizeChanged += (_, _) => UpdateStationMapAspectRatio();
        Opened += (_, _) =>
        {
            UpdateMobileTeamLayout();
            UpdateStationMapAspectRatio();
        };
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_viewModel is not null)
        {
            _viewModel.AddStationRequested -= OnAddStationRequested;
            _viewModel.NewPatientRequested -= OnNewPatientRequested;
            _viewModel.PatientSwapConfirmationRequested -= OnPatientSwapConfirmationRequested;
            _viewModel.DischargeRequested -= OnDischargeRequested;
            _viewModel.StationDeletionRequested -= OnStationDeletionRequested;
            _viewModel.PatientDeletionRequested -= OnPatientDeletionRequested;
            _viewModel.BulkComplaintRequested -= OnBulkComplaintRequested;
            _viewModel.AddMobileTeamRequested -= OnAddMobileTeamRequested;
            _viewModel.MobileTeamDeployRequested -= OnMobileTeamDeployRequested;
            _viewModel.MobileTeamLocationRequested -= OnMobileTeamLocationRequested;
            _viewModel.MobileTeamPatientRequested -= OnMobileTeamPatientRequested;
            _viewModel.MobileTeamStandDownRequested -= OnMobileTeamStandDownRequested;
            _viewModel.MobileTeamDischargeRequested -= OnMobileTeamDischargeRequested;
            _viewModel.MobileTeamEditRequested -= OnMobileTeamEditRequested;
            _viewModel.MobileTeamDeletionRequested -= OnMobileTeamDeletionRequested;
            _viewModel.SessionSwitchRequested -= OnSessionSwitchRequested;
            _viewModel.ExternalDisplayRequested -= OnExternalDisplayRequested;
            _viewModel.SessionLockRequested -= OnSessionLockRequested;
            _viewModel.SessionUnlockRequested -= OnSessionUnlockRequested;
            _viewModel.TerminalPairingRequested -= OnTerminalPairingRequested;
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        }

        _viewModel = DataContext as MainViewModel;
        if (_viewModel is not null)
        {
            _viewModel.AddStationRequested += OnAddStationRequested;
            _viewModel.NewPatientRequested += OnNewPatientRequested;
            _viewModel.PatientSwapConfirmationRequested += OnPatientSwapConfirmationRequested;
            _viewModel.DischargeRequested += OnDischargeRequested;
            _viewModel.StationDeletionRequested += OnStationDeletionRequested;
            _viewModel.PatientDeletionRequested += OnPatientDeletionRequested;
            _viewModel.BulkComplaintRequested += OnBulkComplaintRequested;
            _viewModel.AddMobileTeamRequested += OnAddMobileTeamRequested;
            _viewModel.MobileTeamDeployRequested += OnMobileTeamDeployRequested;
            _viewModel.MobileTeamLocationRequested += OnMobileTeamLocationRequested;
            _viewModel.MobileTeamPatientRequested += OnMobileTeamPatientRequested;
            _viewModel.MobileTeamStandDownRequested += OnMobileTeamStandDownRequested;
            _viewModel.MobileTeamDischargeRequested += OnMobileTeamDischargeRequested;
            _viewModel.MobileTeamEditRequested += OnMobileTeamEditRequested;
            _viewModel.MobileTeamDeletionRequested += OnMobileTeamDeletionRequested;
            _viewModel.SessionSwitchRequested += OnSessionSwitchRequested;
            _viewModel.ExternalDisplayRequested += OnExternalDisplayRequested;
            _viewModel.SessionLockRequested += OnSessionLockRequested;
            _viewModel.SessionUnlockRequested += OnSessionUnlockRequested;
            _viewModel.TerminalPairingRequested += OnTerminalPairingRequested;
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        }
    }

    private void OnTerminalPairingRequested(TerminalPairingRequestInfo request)
    {
        Dispatcher.UIThread.Post(async () =>
        {
            await _pairingApprovalGate.WaitAsync();
            try
            {
                if (_viewModel is null || !IsVisible)
                {
                    return;
                }
                if (_viewModel.IsLocked)
                {
                    await _viewModel.DenyTerminalPairingAsync(
                        request.PairingId,
                        "The host is locked. Unlock it before requesting terminal access.");
                    return;
                }

                var decision = await new TerminalPairingApprovalWindow(request)
                    .ShowDialog<TerminalPairingDecision?>(this);
                if (decision?.Approved == true && decision.VerificationCode is not null)
                {
                    var result = await _viewModel.ApproveTerminalPairingAsync(
                        request.PairingId,
                        decision.VerificationCode);
                    if (!result.Approved)
                    {
                        await new MessageWindow("Terminal not approved", result.Message).ShowDialog(this);
                    }
                }
                else
                {
                    await _viewModel.DenyTerminalPairingAsync(request.PairingId);
                }
            }
            finally
            {
                _pairingApprovalGate.Release();
            }
        });
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.IsLocked) && _viewModel?.IsLocked == true)
        {
            Dispatcher.UIThread.Post(() => UnlockDigitBox1.Focus());
        }
        if (e.PropertyName is nameof(MainViewModel.SelectedPage) or nameof(MainViewModel.SelectedArea))
        {
            _isMobileTeamDrawerOpen = false;
            UpdateMobileTeamLayout();
        }
    }

    private async void OnAddStationRequested(object? sender, EventArgs e)
    {
        if (_viewModel is null)
        {
            return;
        }

        var draft = await new AddStationDialog().ShowDialog<StationDraft?>(this);
        if (draft is not null)
        {
            await _viewModel.CreateStationAsync(draft);
        }
    }

    private async void OnAddMobileTeamRequested(object? sender, EventArgs e)
    {
        if (_viewModel is null) return;
        var draft = await new MobileTeamEditorDialog().ShowDialog<MobileTeamDraft?>(this);
        if (draft is not null) await _viewModel.CreateMobileTeamAsync(draft);
    }

    private async void OnMobileTeamEditRequested(MobileTeamViewModel team)
    {
        if (_viewModel is null) return;
        var draft = await new MobileTeamEditorDialog(team.Callsign, team.Note).ShowDialog<MobileTeamDraft?>(this);
        if (draft is not null) await _viewModel.UpdateMobileTeamAsync(team, draft);
    }

    private async void OnMobileTeamDeployRequested(MobileTeamViewModel team)
    {
        if (_viewModel is null) return;
        var location = await new MobileTeamDeploymentDialog(team.Callsign).ShowDialog<string?>(this);
        if (location is not null) await _viewModel.DeployMobileTeamAsync(team, location);
    }

    private async void OnMobileTeamLocationRequested(MobileTeamViewModel team)
    {
        if (_viewModel is null) return;
        var location = await new MobileTeamDeploymentDialog(team.Callsign, team.DeploymentLocation, true).ShowDialog<string?>(this);
        if (location is not null) await _viewModel.UpdateMobileTeamLocationAsync(team, location);
    }

    private async void OnMobileTeamPatientRequested(MobileTeamViewModel team)
    {
        if (_viewModel is null) return;
        var draft = await new NewPatientDialog(team.Callsign).ShowDialog<NewPatientDraft?>(this);
        if (draft is not null) await _viewModel.SubmitNewMobileTeamPatientAsync(team, draft);
    }

    private async void OnMobileTeamDischargeRequested(MobileTeamViewModel team)
    {
        if (_viewModel is null || team.CurrentPatient is null) return;
        var draft = await new DischargePatientDialog(team.Callsign, _viewModel.DischargeRoutes, _viewModel.DischargeOutcomes).ShowDialog<DischargePatientDraft?>(this);
        if (draft is not null) await _viewModel.CompleteMobileTeamDischargeAsync(team, draft.Route, draft.Outcome, false);
    }

    private async void OnMobileTeamStandDownRequested(MobileTeamViewModel team)
    {
        if (_viewModel is null) return;
        if (team.CurrentPatient is null)
        {
            await _viewModel.StandDownMobileTeamAsync(team);
            return;
        }

        var available = _viewModel.Stations.Where(station => !station.IsOccupied).ToList();
        var request = await new StandDownMobileTeamDialog(team.Callsign, available).ShowDialog<StandDownRequest?>(this);
        if (request?.Outcome == StandDownOutcome.Transfer && request.StationId is Guid stationId)
        {
            await _viewModel.TransferTeamPatientAndStandDownAsync(team, stationId);
        }
        else if (request?.Outcome == StandDownOutcome.Discharge)
        {
            var draft = await new DischargePatientDialog(team.Callsign, _viewModel.DischargeRoutes, _viewModel.DischargeOutcomes).ShowDialog<DischargePatientDraft?>(this);
            if (draft is not null) await _viewModel.CompleteMobileTeamDischargeAsync(team, draft.Route, draft.Outcome, true);
        }
    }

    private async void OnMobileTeamDeletionRequested(MobileTeamViewModel team)
    {
        if (_viewModel is null) return;
        var confirmed = await new MessageWindow(
            "Delete mobile team",
            $"Remove {team.Callsign} from this shift? Historical patient events will be retained.",
            true,
            "Delete team").ShowDialog<bool>(this);
        if (confirmed) await _viewModel.ConfirmDeleteMobileTeamAsync(team);
    }

    private async void OnNewPatientRequested(StationViewModel station)
    {
        if (_viewModel is null)
        {
            return;
        }

        var draft = await new NewPatientDialog(station.Name).ShowDialog<NewPatientDraft?>(this);
        if (draft is not null)
        {
            await _viewModel.SubmitNewPatientAsync(station, draft);
        }
    }

    private async void OnPatientSwapConfirmationRequested(StationViewModel source, StationViewModel destination)
    {
        if (_viewModel is null)
        {
            return;
        }

        var confirmed = await new ConfirmPatientSwapDialog(source.Name, destination.Name).ShowDialog<bool>(this);
        if (confirmed)
        {
            await _viewModel.ConfirmPatientSwapAsync(source, destination);
        }
    }

    private async void OnDischargeRequested(StationViewModel station)
    {
        if (_viewModel is null) return;
        var draft = await new DischargePatientDialog(station.Name, _viewModel.DischargeRoutes, _viewModel.DischargeOutcomes).ShowDialog<DischargePatientDraft?>(this);
        if (draft is not null) await _viewModel.CompleteDischargeAsync(station, draft.Route, draft.Outcome);
    }

    private async void OnStationDeletionRequested(StationViewModel station)
    {
        if (_viewModel is null) return;
        var confirmed = await new MessageWindow(
            "Delete station",
            $"Remove {station.Name} from the active treatment-centre map and tables? Historical shift data will be retained.",
            true,
            "Delete station").ShowDialog<bool>(this);
        if (confirmed) await _viewModel.ConfirmDeleteStationAsync(station);
    }

    private async void OnPatientDeletionRequested(PatientViewModel patient)
    {
        if (_viewModel is null) return;
        var activeWarning = patient.IsDischarged
            ? ""
            : " This patient is currently active; their station or mobile team will become available.";
        var confirmed = await new MessageWindow(
            "Delete patient",
            $"Delete Patient {patient.PatientNumber} and their recorded lifecycle from this shift? This cannot be undone.{activeWarning}",
            true,
            "Delete patient").ShowDialog<bool>(this);
        if (confirmed) await _viewModel.ConfirmDeletePatientAsync(patient);
    }

    private async void OnBulkComplaintRequested(int selectedCount)
    {
        if (_viewModel is null) return;
        var complaint = await new TextEntryWindow(
            "Set presenting complaint",
            $"Presenting complaint for {selectedCount} selected patient{(selectedCount == 1 ? "" : "s")}")
            .ShowDialog<string?>(this);
        if (complaint is not null)
        {
            await _viewModel.ApplyBulkComplaintAsync(complaint);
        }
    }

    private async void OnStationOrderPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control { DataContext: StationViewModel { IsEditMode: true } station }
            || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        e.Handled = true;
        var data = new DataTransfer();
        data.Add(DataTransferItem.Create(StationOrderFormat, station.Id.ToString("N")));
        await DragDrop.DoDragDropAsync(e, data, DragDropEffects.Move);
        ClearStationOrderDropTargets();
    }

    private async void OnTablePatientPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        var patient = (sender as Control)?.DataContext switch
        {
            StationViewModel station => station.CurrentPatient,
            MobileTeamViewModel team => team.CurrentPatient,
            _ => null
        };
        if (patient is null) return;

        e.Handled = true;
        var data = new DataTransfer();
        data.Add(DataTransferItem.Create(PatientDragData.Format, patient.Uid.ToString("N")));
        await DragDrop.DoDragDropAsync(e, data, DragDropEffects.Move);
    }

    private void OnStationOrderDragOver(object? sender, DragEventArgs e)
    {
        var patientUid = e.DataTransfer.TryGetValue(PatientDragData.Format);
        if (sender is Border { DataContext: StationViewModel { IsOperationalMode: true } patientTarget }
            && patientUid is not null)
        {
            patientTarget.IsDropTarget = true;
            e.DragEffects = DragDropEffects.Move;
            return;
        }

        var sourceId = e.DataTransfer.TryGetValue(StationOrderFormat);
        if (sender is Border { DataContext: StationViewModel { IsEditMode: true } target } row
            && sourceId is not null
            && !string.Equals(sourceId, target.Id.ToString("N"), StringComparison.OrdinalIgnoreCase))
        {
            ClearStationOrderDropTargets();
            target.IsStationOrderDropTarget = true;
            target.IsStationOrderDropAfter = e.GetPosition(row).Y >= row.Bounds.Height / 2;
            e.DragEffects = DragDropEffects.Move;
            return;
        }

        e.DragEffects = DragDropEffects.None;
    }

    private void OnStationOrderDragLeave(object? sender, RoutedEventArgs e)
    {
        if (sender is Border { DataContext: StationViewModel target })
        {
            target.IsStationOrderDropTarget = false;
            target.IsStationOrderDropAfter = false;
            target.IsDropTarget = false;
        }
    }

    private async void OnStationOrderDrop(object? sender, DragEventArgs e)
    {
        var patientUid = e.DataTransfer.TryGetValue(PatientDragData.Format);
        if (sender is Border { DataContext: StationViewModel { IsOperationalMode: true } patientTarget }
            && patientUid is not null
            && Guid.TryParse(patientUid, out var parsedPatientUid))
        {
            patientTarget.IsDropTarget = false;
            await patientTarget.DropPatientAsync(parsedPatientUid);
            e.Handled = true;
            return;
        }

        var sourceId = e.DataTransfer.TryGetValue(StationOrderFormat);
        if (_viewModel is null
            || sender is not Border { DataContext: StationViewModel { IsEditMode: true } target } row
            || sourceId is null
            || !Guid.TryParse(sourceId, out var sourceStationId))
        {
            return;
        }

        var placeAfter = e.GetPosition(row).Y >= row.Bounds.Height / 2;
        target.IsStationOrderDropTarget = false;
        target.IsStationOrderDropAfter = false;
        await _viewModel.ReorderStationAsync(sourceStationId, target.Id, placeAfter);
        e.Handled = true;
    }

    private void OnMobileTeamRowDragOver(object? sender, DragEventArgs e)
    {
        var patientUid = e.DataTransfer.TryGetValue(PatientDragData.Format);
        if (sender is Border { DataContext: MobileTeamViewModel { CanAcceptPatientDrop: true } target }
            && patientUid is not null)
        {
            target.IsDropTarget = true;
            e.DragEffects = DragDropEffects.Move;
            return;
        }

        e.DragEffects = DragDropEffects.None;
    }

    private void OnMobileTeamRowDragLeave(object? sender, RoutedEventArgs e)
    {
        if (sender is Border { DataContext: MobileTeamViewModel target }) target.IsDropTarget = false;
    }

    private async void OnMobileTeamRowDrop(object? sender, DragEventArgs e)
    {
        var patientUid = e.DataTransfer.TryGetValue(PatientDragData.Format);
        if (sender is not Border { DataContext: MobileTeamViewModel { CanAcceptPatientDrop: true } target }
            || patientUid is null
            || !Guid.TryParse(patientUid, out var parsedPatientUid))
        {
            return;
        }

        target.IsDropTarget = false;
        await target.DropPatientAsync(parsedPatientUid);
        e.Handled = true;
    }

    private void ClearStationOrderDropTargets()
    {
        if (_viewModel is null) return;
        foreach (var station in _viewModel.Stations)
        {
            station.IsStationOrderDropTarget = false;
            station.IsStationOrderDropAfter = false;
        }
    }

    private async Task OpenExternalDisplayAsync(ExternalDisplayMode mode)
    {
        var current = Screens.ScreenFromWindow(this);
        var target = Screens.All.FirstOrDefault(screen => screen != current);
        if (target is null)
        {
            await new MessageWindow("External display", "Connect a second monitor before opening an external display.").ShowDialog(this);
            return;
        }
        _externalDisplay?.Close();
        _externalDisplay = new ExternalDisplayWindow(_viewModel!, mode) { Position = target.Bounds.Position, Width = target.Bounds.Width, Height = target.Bounds.Height, WindowState = WindowState.FullScreen };
        _externalDisplay.Show();
    }

    private void OnSessionSwitchRequested(object? sender, EventArgs e) => App.ShowRecentSessions(this);
    private async void OnExternalDisplayRequested(ExternalDisplayMode mode) => await OpenExternalDisplayAsync(mode);
    private async void OnSessionLockRequested(object? sender, EventArgs e)
    {
        _externalDisplay?.Close();
        try
        {
            await App.SealActiveSessionAsync();
            _viewModel?.CompleteLock();
        }
        catch (Exception exception)
        {
            _viewModel?.ReportPersistenceFailure($"Unable to secure the shift: {exception.Message}");
        }
    }
    private async void OnSessionUnlockRequested(object? sender, EventArgs e) { try { await App.UnsealActiveSessionAsync(); _viewModel?.CompleteUnlock(); } catch { _viewModel?.CompleteLock(); } }
    private void OnFullScreenClicked(object? sender, RoutedEventArgs e) => ToggleFullScreen();

    private async void OnSafeExitClicked(object? sender, RoutedEventArgs e)
    {
        var confirmed = await new MessageWindow("Exit TCM+", "The active shift will be sealed before TCM+ closes.", true, "Exit").ShowDialog<bool>(this);
        if (!confirmed) return;
        _externalDisplay?.Close();
        Close();
    }

    private void OnToggleMobileTeamDrawer(object? sender, RoutedEventArgs e)
    {
        _isMobileTeamDrawerOpen = !_isMobileTeamDrawerOpen;
        UpdateMobileTeamLayout();
    }

    private void OnCloseMobileTeamDrawer(object? sender, RoutedEventArgs e)
    {
        _isMobileTeamDrawerOpen = false;
        UpdateMobileTeamLayout();
    }

    private void UpdateMobileTeamLayout()
    {
        if (ManagerMapGrid is null || ManagerMapGrid.ColumnDefinitions.Count < 5)
        {
            return;
        }

        var wide = ClientSize.Width >= 1450;
        ManagerMapGrid.ColumnDefinitions[4].Width = new GridLength(wide ? 320 : 0);
        ManagerMapGrid.ColumnDefinitions[3].Width = new GridLength(wide ? 16 : 0);
        DockedMobileTeamRail.IsVisible = wide;
        MobileTeamDrawerButton.IsVisible = !wide;
        MobileTeamDrawer.IsVisible = !wide && _isMobileTeamDrawerOpen && _viewModel?.IsMapPage == true;
        if (wide) _isMobileTeamDrawerOpen = false;
        Dispatcher.UIThread.Post(UpdateStationMapAspectRatio);
    }

    private void UpdateStationMapAspectRatio()
    {
        var availableWidth = StationMapHost.Bounds.Width;
        var availableHeight = StationMapHost.Bounds.Height;
        if (availableWidth <= 0 || availableHeight <= 0)
        {
            return;
        }

        if (availableWidth / availableHeight > StationMapAspectRatio)
        {
            StationMapCard.Height = availableHeight;
            StationMapCard.Width = availableHeight * StationMapAspectRatio;
        }
        else
        {
            StationMapCard.Width = availableWidth;
            StationMapCard.Height = availableWidth / StationMapAspectRatio;
        }
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && _isMobileTeamDrawerOpen)
        {
            _isMobileTeamDrawerOpen = false;
            UpdateMobileTeamLayout();
            e.Handled = true;
            return;
        }

        if (e.Key != Key.F11)
        {
            return;
        }

        ToggleFullScreen();
        e.Handled = true;
    }

    private void ToggleFullScreen()
    {
        if (WindowState == WindowState.FullScreen)
        {
            WindowState = _windowStateBeforeFullScreen;
        }
        else
        {
            _windowStateBeforeFullScreen = WindowState;
            WindowState = WindowState.FullScreen;
        }
    }

    private void OnUnlockDigitChanged(object? sender, TextChangedEventArgs e)
    {
        if (sender is not TextBox textBox || string.IsNullOrEmpty(textBox.Text))
        {
            return;
        }

        if (textBox.Text.Length > 1)
        {
            textBox.Text = textBox.Text[^1].ToString();
        }

        NextUnlockInput(textBox)?.Focus();
    }

    private void OnUnlockDigitKeyDown(object? sender, KeyEventArgs e)
    {
        if (sender is not TextBox textBox)
        {
            return;
        }

        if (e.Key == Key.Back && string.IsNullOrEmpty(textBox.Text))
        {
            PreviousUnlockInput(textBox)?.Focus();
        }
        else if (e.Key == Key.Enter)
        {
            if (NextUnlockInput(textBox) is { } next) next.Focus();
            else if (_viewModel?.UnlockCommand.CanExecute(null) == true) _viewModel.UnlockCommand.Execute(null);
            e.Handled = true;
        }
    }

    private TextBox? NextUnlockInput(TextBox input)
    {
        var inputs = UnlockInputs;
        var index = Array.IndexOf(inputs, input);
        return index >= 0 && index < inputs.Length - 1 ? inputs[index + 1] : null;
    }

    private TextBox? PreviousUnlockInput(TextBox input)
    {
        var inputs = UnlockInputs;
        var index = Array.IndexOf(inputs, input);
        return index > 0 ? inputs[index - 1] : null;
    }

    private TextBox[] UnlockInputs => [UnlockDigitBox1, UnlockDigitBox2, UnlockDigitBox3, UnlockDigitBox4, UnlockDigitBox5, UnlockDigitBox6];
}
