using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using TCMPlus.App.ViewModels;
using TCMPlus.Domain.Models;

namespace TCMPlus.App.Views;

public partial class MainWindow : Window
{
    private MainViewModel? _viewModel;
    private WindowState _windowStateBeforeFullScreen = WindowState.Normal;
    private ExternalDisplayWindow? _externalDisplay;

    public MainWindow()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
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
            _viewModel.SessionSwitchRequested -= OnSessionSwitchRequested;
            _viewModel.ExternalDisplayRequested -= OnExternalDisplayRequested;
            _viewModel.SessionLockRequested -= OnSessionLockRequested;
            _viewModel.SessionUnlockRequested -= OnSessionUnlockRequested;
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
            _viewModel.SessionSwitchRequested += OnSessionSwitchRequested;
            _viewModel.ExternalDisplayRequested += OnExternalDisplayRequested;
            _viewModel.SessionLockRequested += OnSessionLockRequested;
            _viewModel.SessionUnlockRequested += OnSessionUnlockRequested;
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        }
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.IsLocked) && _viewModel?.IsLocked == true)
        {
            Dispatcher.UIThread.Post(() => UnlockDigitBox1.Focus());
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
            : " This patient is currently active; their station will become available.";
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

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
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
