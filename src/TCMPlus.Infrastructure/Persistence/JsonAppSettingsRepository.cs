using System.Text.Json;
using TCMPlus.Domain.Models;
using TCMPlus.Domain.Persistence;

namespace TCMPlus.Infrastructure.Persistence;

public sealed class JsonAppSettingsRepository : IAppSettingsRepository
{
    private readonly string _path;

    public JsonAppSettingsRepository(string? root = null)
    {
        root ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TCMPlus");
        Directory.CreateDirectory(root);
        _path = Path.Combine(root, "app-settings.json");
    }

    public async Task<AppSettings> GetAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_path)) return AppSettings.Default;
        await using var stream = File.OpenRead(_path);
        return await JsonSerializer.DeserializeAsync<AppSettings>(stream, cancellationToken: cancellationToken) ?? AppSettings.Default;
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        var routes = settings.DischargeRoutes.Where(route => !string.IsNullOrWhiteSpace(route)).Select(route => route.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (routes.Count == 0) throw new InvalidOperationException("Keep at least one discharge route.");
        await using var stream = File.Create(_path);
        await JsonSerializer.SerializeAsync(stream, new AppSettings(routes), cancellationToken: cancellationToken);
    }
}
