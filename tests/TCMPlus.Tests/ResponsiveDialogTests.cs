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

    [Fact]
    public void Axaml_uses_vector_icons_and_has_no_placeholder_or_icon_font_glyphs()
    {
        var appRoot = Path.Combine(RepositoryRoot(), "src", "TCMPlus.App");
        foreach (var path in Directory.EnumerateFiles(appRoot, "*.axaml", SearchOption.AllDirectories))
        {
            var content = File.ReadAllText(path);
            Assert.DoesNotContain("Segoe Fluent Icons", content, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain('\uFFFD', content);
            Assert.DoesNotContain("⠿", content, StringComparison.Ordinal);
            Assert.DoesNotContain("□", content, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Pin_interfaces_use_one_pasteable_six_digit_field()
    {
        var views = Path.Combine(RepositoryRoot(), "src", "TCMPlus.App", "Views");
        var shiftSetup = File.ReadAllText(Path.Combine(views, "ShiftSetupWindow.axaml"));
        var main = File.ReadAllText(Path.Combine(views, "MainWindow.axaml"));

        Assert.DoesNotContain("PinDigitBox", shiftSetup, StringComparison.Ordinal);
        Assert.DoesNotContain("UnlockDigitBox", main, StringComparison.Ordinal);
        Assert.Contains("ShiftPinInput", shiftSetup, StringComparison.Ordinal);
        Assert.Contains("UnlockPinInput", main, StringComparison.Ordinal);
        Assert.Contains("MaxLength=\"6\"", shiftSetup, StringComparison.Ordinal);
        Assert.DoesNotContain("0.13.0-DEV", shiftSetup, StringComparison.Ordinal);
        Assert.Contains("VersionLabel", shiftSetup, StringComparison.Ordinal);
    }

    [Fact]
    public void Patient_correction_surface_never_offers_add_patient()
    {
        var main = File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "TCMPlus.App", "Views", "MainWindow.axaml"));
        var start = main.IndexOf("IsPatientsPage", StringComparison.Ordinal);
        var end = main.IndexOf("IsSetupPage", start, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start);
        var patientsSection = main[start..end];
        Assert.DoesNotContain("Add patient", patientsSection, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Window_chrome_and_interaction_size_contract_is_declared()
    {
        var root = RepositoryRoot();
        var appStyles = File.ReadAllText(Path.Combine(root, "src", "TCMPlus.App", "App.axaml"));
        var main = File.ReadAllText(Path.Combine(root, "src", "TCMPlus.App", "Views", "MainWindow.axaml"));
        var external = File.ReadAllText(Path.Combine(root, "src", "TCMPlus.App", "Views", "ExternalDisplayWindow.axaml"));

        Assert.Contains("ControlMinHeight", appStyles, StringComparison.Ordinal);
        Assert.Contains("WindowDecorations=\"None\"", main, StringComparison.Ordinal);
        Assert.Contains("WindowDecorations=\"None\"", external, StringComparison.Ordinal);
        Assert.DoesNotContain("WindowControlButtons", main, StringComparison.Ordinal);
        Assert.Contains("OnSafeExitClicked", main, StringComparison.Ordinal);
        Assert.Contains("WindowControlButtons", external, StringComparison.Ordinal);
    }

    [Fact]
    public void Startup_choices_are_embedded_in_one_window()
    {
        var root = RepositoryRoot();
        var setup = File.ReadAllText(Path.Combine(root, "src", "TCMPlus.App", "Views", "ShiftSetupWindow.axaml"));
        var app = File.ReadAllText(Path.Combine(root, "src", "TCMPlus.App", "App.axaml.cs"));

        Assert.Contains("RecentSessionsView", setup, StringComparison.Ordinal);
        Assert.Contains("TerminalConnectView", setup, StringComparison.Ordinal);
        Assert.Contains("OnShowSavedShifts", setup, StringComparison.Ordinal);
        Assert.Contains("OnShowTerminal", setup, StringComparison.Ordinal);
        Assert.DoesNotContain("ShowTerminalConnection", app, StringComparison.Ordinal);
    }

    [Fact]
    public void Main_window_declares_compact_bounds_and_a_fixed_ratio_map_viewport()
    {
        var root = RepositoryRoot();
        var main = File.ReadAllText(Path.Combine(root, "src", "TCMPlus.App", "Views", "MainWindow.axaml"));
        var codeBehind = File.ReadAllText(Path.Combine(root, "src", "TCMPlus.App", "Views", "MainWindow.axaml.cs"));

        Assert.Contains("MinWidth=\"1024\"", main, StringComparison.Ordinal);
        Assert.Contains("MinHeight=\"600\"", main, StringComparison.Ordinal);
        Assert.Contains("Name=\"StationMapViewport\"", main, StringComparison.Ordinal);
        Assert.Contains("StationMapAspectRatio = 5d / 3d", codeBehind, StringComparison.Ordinal);
    }

    [Fact]
    public void Shared_styles_keep_selection_and_disabled_affordances_visible()
    {
        var appStyles = File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "TCMPlus.App", "App.axaml"));

        Assert.Contains("ComboBoxDropDownGlyphForeground", appStyles, StringComparison.Ordinal);
        Assert.Contains("CheckBoxCheckBackgroundStrokeUnchecked", appStyles, StringComparison.Ordinal);
        Assert.Contains("<Style Selector=\"Button:disabled\">", appStyles, StringComparison.Ordinal);
        Assert.Contains("<ControlTemplate>", appStyles, StringComparison.Ordinal);
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Directory.Build.props")))
        {
            directory = directory.Parent;
        }
        return directory?.FullName ?? throw new InvalidOperationException("Could not locate the repository root.");
    }
}
