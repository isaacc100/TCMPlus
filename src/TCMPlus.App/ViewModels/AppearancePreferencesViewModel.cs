using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TCMPlus.Domain.Models;
using TCMPlus.Infrastructure.Persistence;

namespace TCMPlus.App.ViewModels;

public partial class AppearancePreferencesViewModel(DevicePreferencesStore store) : ViewModelBase
{
    private bool _isInitializing;
    private static DevicePreferences _current = DevicePreferences.Default;

    [ObservableProperty] private AccessibilityPreset _selectedPreset = AccessibilityPreset.Default;
    [ObservableProperty] private UiFontChoice _font = UiFontChoice.Inter;
    [ObservableProperty] private double _textScale = 1d;
    [ObservableProperty] private UiThemePreference _theme = UiThemePreference.System;
    [ObservableProperty] private double _spacingScale = 1d;
    [ObservableProperty] private ReducedMotionPreference _reducedMotion = ReducedMotionPreference.System;
    [ObservableProperty] private ColorVisionPalette _colorVisionPalette = ColorVisionPalette.Default;
    [ObservableProperty] private bool _easyRead;
    [ObservableProperty] private bool _enhancedKeyboard;
    [ObservableProperty] private ExternalDisplayMode _externalDisplayMode = ExternalDisplayMode.Dashboard;
    [ObservableProperty] private string? _preferredMonitorId;

    public IReadOnlyList<AccessibilityPreset> Presets { get; } =
        Enum.GetValues<AccessibilityPreset>().Where(value => value != AccessibilityPreset.Custom).ToList();
    public IReadOnlyList<UiFontChoice> Fonts { get; } = Enum.GetValues<UiFontChoice>();
    public IReadOnlyList<double> TextScales { get; } = [1d, 1.25d, 1.5d, 1.75d, 2d];
    public IReadOnlyList<UiThemePreference> Themes { get; } = Enum.GetValues<UiThemePreference>();
    public IReadOnlyList<double> SpacingScales { get; } = [1d, 1.25d, 1.5d];
    public IReadOnlyList<ReducedMotionPreference> MotionPreferences { get; } = Enum.GetValues<ReducedMotionPreference>();
    public IReadOnlyList<ColorVisionPalette> ColorVisionPalettes { get; } = Enum.GetValues<ColorVisionPalette>();
    public IReadOnlyList<ExternalDisplayMode> ExternalDisplayModes { get; } = Enum.GetValues<ExternalDisplayMode>();

    public double BaseFontSize => 15d * TextScale;
    public double ControlHeight => 44d * SpacingScale;
    public FontFamily FontFamily => ResolveFont(Font);
    public string TextScaleText => $"{TextScale:P0}";
    public string SpacingScaleText => $"{SpacingScale:0.##}x";
    public bool ShowShortcutHints => EnhancedKeyboard;
    public bool ReduceSecondaryInformation => EasyRead;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        _isInitializing = true;
        try
        {
            SetFrom(await store.LoadAsync(cancellationToken));
        }
        finally
        {
            _isInitializing = false;
        }

        ApplyLivePreview(CurrentPreferences());
    }

    public static async Task InitializeApplicationAsync(
        DevicePreferencesStore preferencesStore,
        CancellationToken cancellationToken = default) =>
        ApplyLivePreview(await preferencesStore.LoadAsync(cancellationToken));

    [RelayCommand]
    private void Reset()
    {
        var displayMode = ExternalDisplayMode;
        var monitor = PreferredMonitorId;
        SetFrom(DevicePreferences.Default with
        {
            ExternalDisplayMode = displayMode,
            PreferredMonitorId = monitor
        });
        _ = SaveAndApplyAsync();
    }

    partial void OnSelectedPresetChanged(AccessibilityPreset value)
    {
        if (_isInitializing || value == AccessibilityPreset.Custom)
        {
            return;
        }

        _isInitializing = true;
        try
        {
            SetFrom(CurrentPreferences().ApplyPreset(value));
        }
        finally
        {
            _isInitializing = false;
        }

        _ = SaveAndApplyAsync();
    }

    partial void OnFontChanged(UiFontChoice value) => OnOverrideChanged();
    partial void OnTextScaleChanged(double value)
    {
        OnPropertyChanged(nameof(BaseFontSize));
        OnPropertyChanged(nameof(TextScaleText));
        OnOverrideChanged();
    }
    partial void OnThemeChanged(UiThemePreference value) => OnOverrideChanged();
    partial void OnSpacingScaleChanged(double value)
    {
        OnPropertyChanged(nameof(ControlHeight));
        OnPropertyChanged(nameof(SpacingScaleText));
        OnOverrideChanged();
    }
    partial void OnReducedMotionChanged(ReducedMotionPreference value) => OnOverrideChanged();
    partial void OnColorVisionPaletteChanged(ColorVisionPalette value) => OnOverrideChanged();
    partial void OnEasyReadChanged(bool value)
    {
        OnPropertyChanged(nameof(ReduceSecondaryInformation));
        OnOverrideChanged();
    }
    partial void OnEnhancedKeyboardChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowShortcutHints));
        OnOverrideChanged();
    }
    partial void OnExternalDisplayModeChanged(ExternalDisplayMode value) => OnOverrideChanged(false);
    partial void OnPreferredMonitorIdChanged(string? value) => OnOverrideChanged(false);

    private void OnOverrideChanged(bool markCustom = true)
    {
        OnPropertyChanged(nameof(FontFamily));
        if (_isInitializing)
        {
            return;
        }

        if (markCustom && SelectedPreset != AccessibilityPreset.Custom)
        {
            _isInitializing = true;
            SelectedPreset = AccessibilityPreset.Custom;
            _isInitializing = false;
        }

        _ = SaveAndApplyAsync();
    }

    private async Task SaveAndApplyAsync()
    {
        var preferences = CurrentPreferences().Normalize();
        ApplyLivePreview(preferences);
        await store.SaveAsync(preferences);
    }

    private DevicePreferences CurrentPreferences() => new(
        Font,
        TextScale,
        Theme,
        SpacingScale,
        ReducedMotion,
        ColorVisionPalette,
        EasyRead,
        EnhancedKeyboard,
        ExternalDisplayMode,
        PreferredMonitorId,
        SelectedPreset);

    private void SetFrom(DevicePreferences preferences)
    {
        SelectedPreset = preferences.SelectedPreset;
        Font = preferences.Font;
        TextScale = preferences.TextScale;
        Theme = preferences.Theme;
        SpacingScale = preferences.SpacingScale;
        ReducedMotion = preferences.ReducedMotion;
        ColorVisionPalette = preferences.ColorVisionPalette;
        EasyRead = preferences.EasyRead;
        EnhancedKeyboard = preferences.EnhancedKeyboard;
        ExternalDisplayMode = preferences.ExternalDisplayMode;
        PreferredMonitorId = preferences.PreferredMonitorId;
    }

    private static void ApplyLivePreview(DevicePreferences preferences)
    {
        _current = preferences;
        if (Application.Current is not { } application)
        {
            return;
        }

        application.RequestedThemeVariant = preferences.Theme switch
        {
            UiThemePreference.Light => ThemeVariant.Light,
            UiThemePreference.HighContrast => ThemeVariant.Light,
            _ => ThemeVariant.Default
        };

        ApplyPalette(application, preferences);
        application.Resources["ControlMinHeight"] = 44d * preferences.SpacingScale;
        application.Resources["ControlPadding"] = new Thickness(14d * preferences.SpacingScale, 9d * preferences.SpacingScale);
        application.Resources["FocusRingThickness"] = new Thickness(preferences.EnhancedKeyboard ? 5d : 3d);
        if (application.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
        {
            foreach (var window in desktop.Windows)
            {
                ApplyToWindow(window);
            }
        }
    }

    public static void ApplyToWindow(Window window)
    {
        window.FontFamily = ResolveFont(_current.Font);
        window.FontSize = 15d * _current.TextScale;
    }

    private static FontFamily ResolveFont(UiFontChoice font) => font switch
    {
        UiFontChoice.AtkinsonHyperlegible => new FontFamily("Atkinson Hyperlegible, Inter"),
        UiFontChoice.OpenDyslexic => new FontFamily("OpenDyslexic, Inter"),
        UiFontChoice.System => FontFamily.Default,
        _ => new FontFamily("Inter")
    };

    private static void ApplyPalette(Application application, DevicePreferences preferences)
    {
        var (open, closed, sea) = preferences.ColorVisionPalette switch
        {
            ColorVisionPalette.Protan => ("#0072B2", "#D55E00", "#6C4AA4"),
            ColorVisionPalette.Deutan => ("#0072B2", "#E69F00", "#6C4AA4"),
            ColorVisionPalette.Tritan => ("#009E73", "#CC79A7", "#3B6064"),
            _ => ("#2F8F61", "#B94B4B", "#55828B")
        };

        if (preferences.Theme == UiThemePreference.HighContrast)
        {
            open = "#005A9C";
            closed = "#A00000";
            sea = "#000000";
            SetBrush(application, "CanvasBrush", "#FFFFFF");
            SetBrush(application, "InkBrush", "#000000");
        }
        else
        {
            SetBrush(application, "CanvasBrush", preferences.EasyRead ? "#FFF9E8" : "#F5F8F4");
            SetBrush(application, "InkBrush", "#364958");
        }

        SetBrush(application, "OpenBrush", open);
        SetBrush(application, "ClosedBrush", closed);
        SetBrush(application, "SeaBrush", sea);
    }

    private static void SetBrush(Application application, string key, string color)
    {
        if (application.Resources.TryGetResource(key, application.ActualThemeVariant, out var value)
            && value is SolidColorBrush brush)
        {
            brush.Color = Color.Parse(color);
        }
    }
}
