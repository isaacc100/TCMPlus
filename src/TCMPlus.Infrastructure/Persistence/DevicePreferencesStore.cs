using System.Text.Json;
using TCMPlus.Domain.Models;

namespace TCMPlus.Infrastructure.Persistence;

public sealed class DevicePreferencesStore(string? applicationDataRoot = null)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly string _path = Path.Combine(
        applicationDataRoot ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TCMPlus"),
        "device-preferences.json");
    private readonly SemaphoreSlim _writeGate = new(1, 1);

    public async Task<DevicePreferences> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_path))
        {
            return DevicePreferences.Default;
        }

        try
        {
            await using var stream = File.OpenRead(_path);
            return (await JsonSerializer.DeserializeAsync<DevicePreferences>(stream, JsonOptions, cancellationToken)
                    ?? DevicePreferences.Default).Normalize();
        }
        catch (JsonException)
        {
            return DevicePreferences.Default;
        }
        catch (IOException)
        {
            return DevicePreferences.Default;
        }
    }

    public async Task SaveAsync(DevicePreferences preferences, CancellationToken cancellationToken = default)
    {
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            var directory = Path.GetDirectoryName(_path)!;
            Directory.CreateDirectory(directory);
            var temporaryPath = _path + $".{Guid.NewGuid():N}.tmp";
            try
            {
                await using (var stream = new FileStream(
                                 temporaryPath,
                                 FileMode.CreateNew,
                                 FileAccess.Write,
                                 FileShare.None,
                                 4096,
                                 FileOptions.WriteThrough))
                {
                    await JsonSerializer.SerializeAsync(stream, preferences.Normalize(), JsonOptions, cancellationToken);
                    await stream.FlushAsync(cancellationToken);
                    stream.Flush(true);
                }

                File.Move(temporaryPath, _path, true);
            }
            finally
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
        }
        finally
        {
            _writeGate.Release();
        }
    }
}

public sealed record DevicePreferences(
    UiFontChoice Font = UiFontChoice.Inter,
    double TextScale = 1d,
    UiThemePreference Theme = UiThemePreference.System,
    double SpacingScale = 1d,
    ReducedMotionPreference ReducedMotion = ReducedMotionPreference.System,
    ColorVisionPalette ColorVisionPalette = ColorVisionPalette.Default,
    bool EasyRead = false,
    bool EnhancedKeyboard = false,
    ExternalDisplayMode ExternalDisplayMode = ExternalDisplayMode.Dashboard,
    string? PreferredMonitorId = null,
    AccessibilityPreset SelectedPreset = AccessibilityPreset.Default)
{
    private static readonly double[] SupportedTextScales = [1d, 1.25d, 1.5d, 1.75d, 2d];
    private static readonly double[] SupportedSpacingScales = [1d, 1.25d, 1.5d];

    public static DevicePreferences Default { get; } = new();

    public DevicePreferences Normalize() => this with
    {
        TextScale = Closest(TextScale, SupportedTextScales),
        SpacingScale = Closest(SpacingScale, SupportedSpacingScales),
        PreferredMonitorId = string.IsNullOrWhiteSpace(PreferredMonitorId) ? null : PreferredMonitorId.Trim()
    };

    public DevicePreferences ApplyPreset(AccessibilityPreset preset) => preset switch
    {
        AccessibilityPreset.Default => Default with
        {
            ExternalDisplayMode = ExternalDisplayMode,
            PreferredMonitorId = PreferredMonitorId
        },
        AccessibilityPreset.HighContrast => this with
        {
            Theme = UiThemePreference.HighContrast,
            SelectedPreset = preset
        },
        AccessibilityPreset.DyslexiaFriendly => this with
        {
            Font = UiFontChoice.AtkinsonHyperlegible,
            SpacingScale = 1.5d,
            SelectedPreset = preset
        },
        AccessibilityPreset.LargeText => this with
        {
            TextScale = 1.5d,
            SelectedPreset = preset
        },
        AccessibilityPreset.ReducedMotion => this with
        {
            ReducedMotion = ReducedMotionPreference.On,
            SelectedPreset = preset
        },
        AccessibilityPreset.IncreasedSpacing => this with
        {
            SpacingScale = 1.5d,
            SelectedPreset = preset
        },
        AccessibilityPreset.Simplified => this with
        {
            EasyRead = true,
            SelectedPreset = preset
        },
        AccessibilityPreset.EnhancedKeyboard => this with
        {
            EnhancedKeyboard = true,
            SelectedPreset = preset
        },
        _ => this with { SelectedPreset = AccessibilityPreset.Custom }
    };

    private static double Closest(double value, IReadOnlyList<double> supported) =>
        supported.OrderBy(candidate => Math.Abs(candidate - value)).First();
}

public enum AccessibilityPreset
{
    Default,
    HighContrast,
    DyslexiaFriendly,
    LargeText,
    ReducedMotion,
    IncreasedSpacing,
    Simplified,
    EnhancedKeyboard,
    Custom
}

public enum UiFontChoice
{
    Inter,
    AtkinsonHyperlegible,
    OpenDyslexic,
    System
}

public enum UiThemePreference
{
    System,
    Light,
    HighContrast
}

public enum ReducedMotionPreference
{
    System,
    On,
    Off
}

public enum ColorVisionPalette
{
    Default,
    Protan,
    Deutan,
    Tritan
}
