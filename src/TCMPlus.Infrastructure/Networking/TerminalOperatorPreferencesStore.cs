using System.Text.Json;

namespace TCMPlus.Infrastructure.Networking;

public sealed class TerminalOperatorPreferencesStore(string? applicationDataRoot = null)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly string _path = Path.Combine(
        applicationDataRoot ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TCMPlus"),
        "terminal-operator.json");
    private readonly SemaphoreSlim _writeGate = new(1, 1);

    public async Task<TerminalOperatorPreferences> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_path))
        {
            return TerminalOperatorPreferences.Default;
        }

        try
        {
            await using var stream = File.OpenRead(_path);
            return await JsonSerializer.DeserializeAsync<TerminalOperatorPreferences>(
                       stream,
                       JsonOptions,
                       cancellationToken)
                   ?? TerminalOperatorPreferences.Default;
        }
        catch (JsonException)
        {
            return TerminalOperatorPreferences.Default;
        }
        catch (IOException)
        {
            return TerminalOperatorPreferences.Default;
        }
    }

    public async Task SaveAsync(
        TerminalOperatorPreferences preferences,
        CancellationToken cancellationToken = default)
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
                    await JsonSerializer.SerializeAsync(stream, preferences, JsonOptions, cancellationToken);
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

public sealed record TerminalOperatorPreferences(bool QuickEntry)
{
    public static TerminalOperatorPreferences Default { get; } = new(false);
}
