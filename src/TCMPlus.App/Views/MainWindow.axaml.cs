using Avalonia.Controls;
using Avalonia.Input;
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
        }

        _viewModel = DataContext as MainViewModel;
        if (_viewModel is not null)
        {
            _viewModel.AddStationRequested += OnAddStationRequested;
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
}
