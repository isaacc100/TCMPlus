using Velopack;
using Velopack.Sources;

namespace TCMPlus.App.Updates;

public sealed class VelopackAppUpdateService : IAppUpdateService
{
    private const string RepositoryUrl = "https://github.com/isaacc100/TCMPlus";
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly UpdateManager? _updateManager;
    private readonly string? _initializationError;
    private UpdateInfo? _availableUpdate;

    public VelopackAppUpdateService()
    {
        try
        {
            var developmentBuild = AppUpdateChannel.IsDevelopmentBuild();
            _updateManager = new UpdateManager(
                new GithubSource(RepositoryUrl, accessToken: null, prerelease: developmentBuild),
                new UpdateOptions { ExplicitChannel = AppUpdateChannel.Current });
        }
        catch (PlatformNotSupportedException)
        {
            _initializationError = "Updates are not available for this platform or architecture yet.";
        }
    }

    public async Task<AppUpdateCheckResult> CheckForUpdatesAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            _availableUpdate = null;
            if (_updateManager is null)
            {
                return AppUpdateCheckResult.Unavailable(_initializationError!);
            }

            if (!_updateManager.IsInstalled)
            {
                return AppUpdateCheckResult.Unavailable("Updates are available from an installed TCM+ release.");
            }

            cancellationToken.ThrowIfCancellationRequested();
            var update = await _updateManager.CheckForUpdatesAsync();
            if (update is null)
            {
                return AppUpdateCheckResult.UpToDate(_updateManager.CurrentVersion?.ToString() ?? "this version");
            }

            _availableUpdate = update;
            return AppUpdateCheckResult.Available(
                update.TargetFullRelease.Version.ToString(),
                update.TargetFullRelease.NotesMarkdown);
        }
        catch (Exception)
        {
            return AppUpdateCheckResult.Unavailable("Unable to check for updates. Try again later.");
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<AppUpdateApplyResult> DownloadAndRestartAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_updateManager is null)
            {
                return AppUpdateApplyResult.Unavailable(_initializationError!);
            }

            if (!_updateManager.IsInstalled)
            {
                return AppUpdateApplyResult.Unavailable("Updates are available from an installed TCM+ release.");
            }

            if (_availableUpdate is null)
            {
                return AppUpdateApplyResult.Unavailable("Check for updates before installing one.");
            }

            await _updateManager.DownloadUpdatesAsync(_availableUpdate, progress: null, cancellationToken);
            _updateManager.ApplyUpdatesAndRestart(_availableUpdate);
            return AppUpdateApplyResult.Restarting();
        }
        catch (Exception)
        {
            return AppUpdateApplyResult.Failed("Unable to download or install the update. Try again later.");
        }
        finally
        {
            _gate.Release();
        }
    }
}
