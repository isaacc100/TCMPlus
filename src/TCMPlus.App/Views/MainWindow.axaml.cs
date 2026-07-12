using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using TCMPlus.App.ViewModels;

namespace TCMPlus.App.Views;

public partial class MainWindow : Window
{
    private MainViewModel? _viewModel;
    private WindowState _windowStateBeforeFullScreen = WindowState.Normal;

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
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        }

        _viewModel = DataContext as MainViewModel;
        if (_viewModel is not null)
        {
            _viewModel.AddStationRequested += OnAddStationRequested;
            _viewModel.NewPatientRequested += OnNewPatientRequested;
            _viewModel.PatientSwapConfirmationRequested += OnPatientSwapConfirmationRequested;
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

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.F11)
        {
            return;
        }

        if (WindowState == WindowState.FullScreen)
        {
            WindowState = _windowStateBeforeFullScreen;
        }
        else
        {
            _windowStateBeforeFullScreen = WindowState;
            WindowState = WindowState.FullScreen;
        }

        e.Handled = true;
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
        else if (e.Key == Key.Enter && _viewModel?.UnlockCommand.CanExecute(null) == true)
        {
            _viewModel.UnlockCommand.Execute(null);
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
