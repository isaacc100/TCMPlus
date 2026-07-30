using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace TCMPlus.App.Views;

public abstract class ResponsiveDialogWindow : Window
{
    internal const double WorkingAreaMargin = 24d;

    public ResponsiveDialogWindow()
    {
        Opened += (_, _) =>
        {
            EnsureVerticalScrollFallback();
            ConstrainToActiveWorkingArea();
            Dispatcher.UIThread.Post(
                ConstrainToActiveWorkingArea,
                DispatcherPriority.Loaded);
        };
    }

    private void EnsureVerticalScrollFallback()
    {
        if (Content is not Control content
            || content is ScrollViewer
            || content.GetVisualDescendants().OfType<ScrollViewer>().Any())
        {
            return;
        }

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
