namespace TCMPlus.App.Updates;

public interface IAppUpdateService
{
    Task<AppUpdateCheckResult> CheckForUpdatesAsync(CancellationToken cancellationToken = default);
    Task<AppUpdateApplyResult> DownloadAndRestartAsync(CancellationToken cancellationToken = default);
}

public enum AppUpdateStatus
{
    UpToDate,
    Available,
    Unavailable
}

public sealed record AppUpdateCheckResult(AppUpdateStatus Status, string StatusText, string? Version = null, string? ReleaseNotes = null)
{
    public static AppUpdateCheckResult UpToDate(string version) => new(AppUpdateStatus.UpToDate, $"TCM+ {version} is up to date.");
    public static AppUpdateCheckResult Available(string version, string? releaseNotes) => new(AppUpdateStatus.Available, $"TCM+ {version} is ready to install.", version, releaseNotes);
    public static AppUpdateCheckResult Unavailable(string message) => new(AppUpdateStatus.Unavailable, message);
}

public sealed record AppUpdateApplyResult(bool Started, string StatusText)
{
    public static AppUpdateApplyResult Unavailable(string message) => new(false, message);
    public static AppUpdateApplyResult Failed(string message) => new(false, message);
    public static AppUpdateApplyResult Restarting() => new(true, "Update installed. Restarting TCM+.");
}
