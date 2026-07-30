using System.Text.Json;

namespace TCMPlus.Infrastructure.Networking;

public sealed class TerminalConnectionPreferencesStore(string? applicationDataRoot = null)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };
    private readonly string _path = Path.Combine(
        applicationDataRoot ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TCMPlus"),
        "terminal-connection.json");

    public async Task<TerminalConnectionPreferences> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_path))
        {
            return TerminalConnectionPreferences.Default;
        }

        try
        {
            await using var stream = File.OpenRead(_path);
            var preferences = await JsonSerializer.DeserializeAsync<TerminalConnectionPreferences>(
                stream,
                JsonOptions,
                cancellationToken);
            return preferences is null
                ? TerminalConnectionPreferences.Default
                : Normalize(preferences);
        }
        catch (JsonException)
        {
            return TerminalConnectionPreferences.Default;
        }
        catch (IOException)
        {
            return TerminalConnectionPreferences.Default;
        }
    }

    public async Task SaveAsync(
        TerminalConnectionPreferences preferences,
        CancellationToken cancellationToken = default)
    {
        var normalized = Normalize(preferences);
        var directory = Path.GetDirectoryName(_path)!;
        Directory.CreateDirectory(directory);
        var temporaryPath = _path + ".tmp";
        await using (var stream = File.Create(temporaryPath))
        {
            await JsonSerializer.SerializeAsync(stream, normalized, JsonOptions, cancellationToken);
            await stream.FlushAsync(cancellationToken);
        }

        File.Move(temporaryPath, _path, true);
    }

    private static TerminalConnectionPreferences Normalize(TerminalConnectionPreferences preferences) =>
        new(
            preferences.TerminalName.Trim()[..Math.Min(preferences.TerminalName.Trim().Length, 48)],
            preferences.HostIdentifier.Trim()[..Math.Min(preferences.HostIdentifier.Trim().Length, 255)]);
}

public sealed record TerminalConnectionPreferences(string TerminalName, string HostIdentifier)
{
    public static TerminalConnectionPreferences Default { get; } = new(
        Environment.MachineName[..Math.Min(Environment.MachineName.Length, 48)],
        string.Empty);
}
