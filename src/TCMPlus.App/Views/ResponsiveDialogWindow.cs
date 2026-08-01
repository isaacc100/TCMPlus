using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Platform;
using Avalonia.Media;
using Avalonia.Threading;
using TCMPlus.App.Controls;
using TCMPlus.App.ViewModels;

namespace TCMPlus.App.Views;

public abstract class ResponsiveDialogWindow : Window
{
    internal const double WorkingAreaMargin = 24d;
    internal const double CustomChromeHeight = 38d;

    public ResponsiveDialogWindow()
    {
        WindowDecorations = Avalonia.Controls.WindowDecorations.None;
        ExtendClientAreaToDecorationsHint = false;
        Opened += (_, _) =>
        {
            AppearancePreferencesViewModel.ApplyToWindow(this);
            EnsureVerticalScrollFallback();
            EnsureCustomChrome();
            ApplyModalBackdrop();
            ConstrainToActiveWorkingArea();
            Dispatcher.UIThread.Post(
                ConstrainToActiveWorkingArea,
                DispatcherPriority.Loaded);
        };
        Closed += (_, _) => RemoveModalBackdrop();
    }

    private bool _customChromeApplied;
    private Panel? _ownerBackdropPanel;
    private Border? _ownerBackdrop;

    private void EnsureCustomChrome()
    {
        if (_customChromeApplied || Content is not Control content)
        {
            return;
        }

        Content = null;
        var host = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,*")
        };
        host.Children.Add(new WindowChrome());
        Grid.SetRow(content, 1);
        host.Children.Add(content);
        Content = new Border
        {
            Background = Brushes.White,
            BorderBrush = new SolidColorBrush(Color.Parse("#6F8D80")),
            BorderThickness = new Thickness(1),
            Child = host
        };
        if (double.IsFinite(Height) && Height > 0)
        {
            // Preserve the dialog's declared content height as well as the narrow
            // frame. Without the two frame pixels, fixed-height dialogs acquire a
            // pointless vertical scrollbar even when their content fits exactly.
            Height += CustomChromeHeight + 2d;
        }
        _customChromeApplied = true;
    }

    private void ApplyModalBackdrop()
    {
        var ownerPanel = FindBackdropPanel(Owner?.Content);
        if (ownerPanel is null || _ownerBackdrop is not null)
        {
            return;
        }

        _ownerBackdropPanel = ownerPanel;
        _ownerBackdrop = new Border
        {
            Background = new SolidColorBrush(Color.Parse("#520F2428")),
            IsHitTestVisible = false,
            ZIndex = 10000
        };
        ownerPanel.Children.Add(_ownerBackdrop);
    }

    private static Panel? FindBackdropPanel(object? content) => content switch
    {
        Panel panel => panel,
        Border { Child: not null } border => FindBackdropPanel(border.Child),
        ContentControl { Content: not null } control => FindBackdropPanel(control.Content),
        _ => null
    };

    private void RemoveModalBackdrop()
    {
        if (_ownerBackdropPanel is not null && _ownerBackdrop is not null)
        {
            _ownerBackdropPanel.Children.Remove(_ownerBackdrop);
        }

        _ownerBackdropPanel = null;
        _ownerBackdrop = null;
    }

    private void EnsureVerticalScrollFallback()
    {
        if (Content is not Control content || content is ScrollViewer)
        {
            return;
        }

        var screen = (Owner is null ? null : Screens.ScreenFromWindow(Owner))
                     ?? Screens.ScreenFromWindow(this)
                     ?? Screens.ScreenFromPoint(Position)
                     ?? Screens.Primary;
        if (screen is null)
        {
            return;
        }

        var scaling = double.IsFinite(screen.Scaling) && screen.Scaling > 0 ? screen.Scaling : 1d;
        var marginPixels = Math.Max(0, (int)Math.Ceiling(WorkingAreaMargin * scaling));
        var maximumHeight = Math.Max(1, screen.WorkingArea.Height - (marginPixels * 2)) / scaling;
        var desiredHeight = (double.IsFinite(Height) && Height > 0 ? Height : Bounds.Height)
                            + CustomChromeHeight + 2d;
        if (desiredHeight <= maximumHeight + 0.5d)
        {
            return;
        }

        Content = null;
        Content = new ScrollViewer
        {
            Content = content,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
    }

    private void ConstrainToActiveWorkingArea()
    {
        var screen = (Owner is null ? null : Screens.ScreenFromWindow(Owner))
                     ?? Screens.ScreenFromWindow(this)
                     ?? Screens.ScreenFromPoint(Position)
                     ?? Screens.Primary;
        if (screen is null)
        {
            return;
        }

        var desiredWidth = double.IsFinite(Width) && Width > 0
            ? Width
            : Math.Max(Bounds.Width, 320d);
        var desiredHeight = double.IsFinite(Height) && Height > 0
            ? Height
            : Math.Max(Bounds.Height, 200d);
        var placement = ResponsiveDialogMetrics.Constrain(
            screen.WorkingArea,
            screen.Scaling,
            new Size(desiredWidth, desiredHeight),
            Position,
            WorkingAreaMargin);

        Classes.Set("compact-dialog", placement.MaximumHeight < 480d);
        MinWidth = Math.Min(MinWidth, placement.Width);
        MinHeight = Math.Min(MinHeight, placement.Height);
        MaxWidth = placement.MaximumWidth;
        MaxHeight = placement.MaximumHeight;
        Width = placement.Width;
        Height = placement.Height;
        Position = placement.Position;
    }
}

internal static class ResponsiveDialogMetrics
{
    public static ResponsiveDialogPlacement Constrain(
        PixelRect workingArea,
        double scaling,
        Size desiredSize,
        PixelPoint desiredPosition,
        double margin)
    {
        scaling = double.IsFinite(scaling) && scaling > 0 ? scaling : 1d;
        var marginPixels = Math.Max(0, (int)Math.Ceiling(margin * scaling));
        var maximumWidthPixels = Math.Max(1, workingArea.Width - (marginPixels * 2));
        var maximumHeightPixels = Math.Max(1, workingArea.Height - (marginPixels * 2));
        var maximumWidth = maximumWidthPixels / scaling;
        var maximumHeight = maximumHeightPixels / scaling;
        var width = Math.Min(Math.Max(1d, desiredSize.Width), maximumWidth);
        var height = Math.Min(Math.Max(1d, desiredSize.Height), maximumHeight);
        var widthPixels = Math.Min(maximumWidthPixels, (int)Math.Ceiling(width * scaling));
        var heightPixels = Math.Min(maximumHeightPixels, (int)Math.Ceiling(height * scaling));
        var minimumX = workingArea.X + marginPixels;
        var minimumY = workingArea.Y + marginPixels;
        var maximumX = Math.Max(minimumX, workingArea.Right - marginPixels - widthPixels);
        var maximumY = Math.Max(minimumY, workingArea.Bottom - marginPixels - heightPixels);

        return new ResponsiveDialogPlacement(
            width,
            height,
            maximumWidth,
            maximumHeight,
            new PixelPoint(
                Math.Clamp(desiredPosition.X, minimumX, maximumX),
                Math.Clamp(desiredPosition.Y, minimumY, maximumY)));
    }
}

internal readonly record struct ResponsiveDialogPlacement(
    double Width,
    double Height,
    double MaximumWidth,
    double MaximumHeight,
    PixelPoint Position);
