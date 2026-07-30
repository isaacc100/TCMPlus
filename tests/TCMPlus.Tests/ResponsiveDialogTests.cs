using Avalonia;
using Avalonia.Controls;
using TCMPlus.App.Views;

namespace TCMPlus.Tests;

public sealed class ResponsiveDialogTests
{
    [Fact]
    public void Every_secondary_window_uses_the_responsive_dialog_base()
    {
        var exceptions = new HashSet<Type>
        {
            typeof(MainWindow),
            typeof(ExternalDisplayWindow)
        };
        var unsafeWindows = typeof(MainWindow).Assembly
            .GetTypes()
            .Where(type =>
                !type.IsAbstract
                && type.Namespace == typeof(MainWindow).Namespace
                && typeof(Window).IsAssignableFrom(type)
                && !exceptions.Contains(type)
                && !typeof(ResponsiveDialogWindow).IsAssignableFrom(type))
            .Select(type => type.FullName)
            .ToList();

        Assert.Empty(unsafeWindows);
    }

    [Theory]
    [InlineData(1d, 650d, 552d)]
    [InlineData(1.5d, 634.667, 352d)]
    [InlineData(2d, 464d, 252d)]
    public void Large_dialogs_fit_a_1024_by_600_working_area_at_common_scaling(
        double scaling,
        double expectedWidth,
        double expectedHeight)
    {
        var placement = ResponsiveDialogMetrics.Constrain(
            new PixelRect(0, 0, 1024, 600),
            scaling,
            new Size(650, 690),
            new PixelPoint(900, 500),
            ResponsiveDialogWindow.WorkingAreaMargin);

        Assert.Equal(expectedWidth, placement.Width, 3);
        Assert.Equal(expectedHeight, placement.Height, 3);
        Assert.True(placement.Position.X >= 0);
        Assert.True(placement.Position.Y >= 0);
        Assert.True(
            placement.Position.X + Math.Ceiling(placement.Width * scaling)
            <= 1024 - Math.Ceiling(ResponsiveDialogWindow.WorkingAreaMargin * scaling));
        Assert.True(
            placement.Position.Y + Math.Ceiling(placement.Height * scaling)
            <= 600 - Math.Ceiling(ResponsiveDialogWindow.WorkingAreaMargin * scaling));
    }

    [Fact]
    public void Dialog_position_is_clamped_to_the_selected_monitor_working_area()
    {
        var placement = ResponsiveDialogMetrics.Constrain(
            new PixelRect(1920, 40, 1920, 1040),
            1.5d,
            new Size(520, 430),
            new PixelPoint(-500, 5000),
            ResponsiveDialogWindow.WorkingAreaMargin);

        Assert.Equal(1956, placement.Position.X);
        Assert.Equal(399, placement.Position.Y);
    }
}
