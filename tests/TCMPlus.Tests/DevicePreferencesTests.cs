using TCMPlus.Domain.Models;
using TCMPlus.Infrastructure.Persistence;

namespace TCMPlus.Tests;

public sealed class DevicePreferencesTests
{
    [Fact]
    public async Task Missing_or_invalid_preferences_use_accessible_defaults()
    {
        var root = CreateTemporaryRoot();
        try
        {
            var store = new DevicePreferencesStore(root);
            Assert.Equal(DevicePreferences.Default, await store.LoadAsync());

            await File.WriteAllTextAsync(Path.Combine(root, "device-preferences.json"), "not json");
            Assert.Equal(DevicePreferences.Default, await store.LoadAsync());
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task Preferences_round_trip_and_normalize_supported_scales()
    {
        var root = CreateTemporaryRoot();
        try
        {
            var store = new DevicePreferencesStore(root);
            await store.SaveAsync(new DevicePreferences(
                UiFontChoice.OpenDyslexic,
                1.6d,
                UiThemePreference.HighContrast,
                1.4d,
                ReducedMotionPreference.On,
                ColorVisionPalette.Deutan,
                true,
                true,
                ExternalDisplayMode.Map,
                "  DISPLAY-2  ",
                AccessibilityPreset.Custom));

            var loaded = await store.LoadAsync();
            Assert.Equal(UiFontChoice.OpenDyslexic, loaded.Font);
            Assert.Equal(1.5d, loaded.TextScale);
            Assert.Equal(1.5d, loaded.SpacingScale);
            Assert.Equal("DISPLAY-2", loaded.PreferredMonitorId);
            Assert.Equal(ExternalDisplayMode.Map, loaded.ExternalDisplayMode);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void Presets_apply_expected_overrides_without_losing_display_selection()
    {
        var current = DevicePreferences.Default with
        {
            ExternalDisplayMode = ExternalDisplayMode.Map,
            PreferredMonitorId = "DISPLAY-2"
        };

        var dyslexia = current.ApplyPreset(AccessibilityPreset.DyslexiaFriendly);
        Assert.Equal(UiFontChoice.AtkinsonHyperlegible, dyslexia.Font);
        Assert.Equal(1.5d, dyslexia.SpacingScale);

        var reset = dyslexia.ApplyPreset(AccessibilityPreset.Default);
        Assert.Equal(ExternalDisplayMode.Map, reset.ExternalDisplayMode);
        Assert.Equal("DISPLAY-2", reset.PreferredMonitorId);
        Assert.Equal(UiFontChoice.Inter, reset.Font);
    }

    [Theory]
    [InlineData(AccessibilityPreset.HighContrast, UiFontChoice.Inter, 1d, UiThemePreference.HighContrast, 1d, ReducedMotionPreference.System, false, false)]
    [InlineData(AccessibilityPreset.DyslexiaFriendly, UiFontChoice.AtkinsonHyperlegible, 1d, UiThemePreference.System, 1.5d, ReducedMotionPreference.System, false, false)]
    [InlineData(AccessibilityPreset.LargeText, UiFontChoice.Inter, 1.5d, UiThemePreference.System, 1d, ReducedMotionPreference.System, false, false)]
    [InlineData(AccessibilityPreset.ReducedMotion, UiFontChoice.Inter, 1d, UiThemePreference.System, 1d, ReducedMotionPreference.On, false, false)]
    [InlineData(AccessibilityPreset.IncreasedSpacing, UiFontChoice.Inter, 1d, UiThemePreference.System, 1.5d, ReducedMotionPreference.System, false, false)]
    [InlineData(AccessibilityPreset.Simplified, UiFontChoice.Inter, 1d, UiThemePreference.System, 1d, ReducedMotionPreference.System, true, false)]
    [InlineData(AccessibilityPreset.EnhancedKeyboard, UiFontChoice.Inter, 1d, UiThemePreference.System, 1d, ReducedMotionPreference.System, false, true)]
    public void Every_preset_has_deterministic_accessibility_tokens(
        AccessibilityPreset preset,
        UiFontChoice font,
        double textScale,
        UiThemePreference theme,
        double spacingScale,
        ReducedMotionPreference motion,
        bool easyRead,
        bool enhancedKeyboard)
    {
        var starting = new DevicePreferences(
            UiFontChoice.OpenDyslexic,
            2d,
            UiThemePreference.HighContrast,
            1.5d,
            ReducedMotionPreference.On,
            ColorVisionPalette.Tritan,
            true,
            true,
            ExternalDisplayMode.Map,
            "DISPLAY-3",
            AccessibilityPreset.Custom);

        var actual = starting.ApplyPreset(preset);

        Assert.Equal(font, actual.Font);
        Assert.Equal(textScale, actual.TextScale);
        Assert.Equal(theme, actual.Theme);
        Assert.Equal(spacingScale, actual.SpacingScale);
        Assert.Equal(motion, actual.ReducedMotion);
        Assert.Equal(easyRead, actual.EasyRead);
        Assert.Equal(enhancedKeyboard, actual.EnhancedKeyboard);
        Assert.Equal(ColorVisionPalette.Default, actual.ColorVisionPalette);
        Assert.Equal(ExternalDisplayMode.Map, actual.ExternalDisplayMode);
        Assert.Equal("DISPLAY-3", actual.PreferredMonitorId);
    }

    private static string CreateTemporaryRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), $"tcmplus-device-preferences-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        return root;
    }
}
