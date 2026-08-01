using Avalonia.Controls;
using Avalonia.Input;

namespace TCMPlus.App.Controls;

public partial class WindowChrome : UserControl
{
    public WindowChrome()
    {
        InitializeComponent();
        AttachedToVisualTree += (_, _) => ConfigureForWindow();
    }

    private Window? OwnerWindow => TopLevel.GetTopLevel(this) as Window;

    private void ConfigureForWindow()
    {
        if (OwnerWindow is not { } window)
        {
            return;
        }

        WindowTitle.Text = string.IsNullOrWhiteSpace(window.Title) ? "TCM+" : window.Title;
        Controls.ShowMinimize = window.CanResize;
        Controls.ShowMaximize = window.CanResize;
    }

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (OwnerWindow is not { } window || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        if (e.ClickCount == 2 && window.CanResize)
        {
            window.WindowState = window.WindowState == WindowState.Maximized
                ? WindowState.Normal
                : WindowState.Maximized;
            e.Handled = true;
            return;
        }

        window.BeginMoveDrag(e);
        e.Handled = true;
    }
}
