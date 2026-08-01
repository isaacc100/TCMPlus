using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;

namespace TCMPlus.App.Controls;

public partial class WindowControlButtons : UserControl
{
    public static readonly StyledProperty<bool> ShowMinimizeProperty =
        AvaloniaProperty.Register<WindowControlButtons, bool>(nameof(ShowMinimize), true);

    public static readonly StyledProperty<bool> ShowMaximizeProperty =
        AvaloniaProperty.Register<WindowControlButtons, bool>(nameof(ShowMaximize), true);

    public WindowControlButtons()
    {
        InitializeComponent();
        AttachedToVisualTree += (_, _) => AttachWindowState();
        DetachedFromVisualTree += (_, _) => DetachWindowState();
    }

    public bool ShowMinimize
    {
        get => GetValue(ShowMinimizeProperty);
        set => SetValue(ShowMinimizeProperty, value);
    }

    public bool ShowMaximize
    {
        get => GetValue(ShowMaximizeProperty);
        set => SetValue(ShowMaximizeProperty, value);
    }

    private Window? OwnerWindow => VisualRoot as Window;
    private Window? _subscribedWindow;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ShowMinimizeProperty || change.Property == ShowMaximizeProperty)
        {
            UpdateVisibility();
        }
    }

    private void AttachWindowState()
    {
        DetachWindowState();
        _subscribedWindow = OwnerWindow;
        if (_subscribedWindow is not null)
        {
            _subscribedWindow.PropertyChanged += OnWindowPropertyChanged;
        }

        UpdateVisibility();
        UpdateMaximizeIcon();
    }

    private void DetachWindowState()
    {
        if (_subscribedWindow is not null)
        {
            _subscribedWindow.PropertyChanged -= OnWindowPropertyChanged;
            _subscribedWindow = null;
        }
    }

    private void OnWindowPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == Window.WindowStateProperty || e.Property == Window.CanResizeProperty)
        {
            UpdateVisibility();
            UpdateMaximizeIcon();
        }
    }

    private void UpdateVisibility()
    {
        var canResize = OwnerWindow?.CanResize == true;
        MinimizeButton.IsVisible = ShowMinimize && canResize;
        MaximizeButton.IsVisible = ShowMaximize && canResize;
    }

    private void UpdateMaximizeIcon()
    {
        MaximizeIcon.Data = Geometry.Parse(OwnerWindow?.WindowState == WindowState.Maximized
            ? "M 6,4 L 16,4 L 16,14 M 4,6 L 14,6 L 14,16 L 4,16 Z"
            : "M 4,4 L 16,4 L 16,16 L 4,16 Z");
    }

    private void OnMinimize(object? sender, RoutedEventArgs e)
    {
        if (OwnerWindow is { } window)
        {
            window.WindowState = WindowState.Minimized;
        }
    }

    private void OnMaximizeRestore(object? sender, RoutedEventArgs e)
    {
        if (OwnerWindow is not { CanResize: true } window)
        {
            return;
        }

        window.WindowState = window.WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;
    }

    private void OnClose(object? sender, RoutedEventArgs e) => OwnerWindow?.Close();
}
