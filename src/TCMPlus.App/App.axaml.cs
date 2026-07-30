using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using TCMPlus.Domain.Persistence;
using TCMPlus.Domain.Services;
using TCMPlus.Domain.Models;
using TCMPlus.Infrastructure.Networking;
using TCMPlus.Infrastructure.Persistence;
using TCMPlus.Infrastructure.Services;
using TCMPlus.Infrastructure.Sessions;
using TCMPlus.App.ViewModels;
using TCMPlus.App.Views;
using TCMPlus.App.LanDisplay;
using TCMPlus.App.TerminalNetworking;
using TCMPlus.App.Updates;

namespace TCMPlus.App;

public partial class App : Application
{
    private static readonly TimeSpan AutosaveInterval = TimeSpan.FromSeconds(30);
    private static readonly EncryptedSessionStore SessionStore = new();
    private static IClassicDesktopStyleApplicationLifetime? _desktop;
    private static ActiveSession? _activeSession;
    private static Mutex? _hostMutex;
    private static bool _hostMutexOwned;
    private static readonly IAppUpdateService UpdateService = new VelopackAppUpdateService();
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            _desktop = desktop;
            CreateShiftSetup(desktop, false);
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static void CreateShiftSetup(IClassicDesktopStyleApplicationLifetime desktop, bool show)
    {
        var shiftSetup = new ShiftSetupWindow();
        shiftSetup.ShiftStarted += async (_, draft) => await OpenShiftAsync(desktop, shiftSetup, draft);
        shiftSetup.LoadExistingRequested += (_, _) => ShowRecentSessions(shiftSetup);
        shiftSetup.TerminalConnectionRequested += (_, _) => ShowTerminalConnection(shiftSetup);
        shiftSetup.UpdateCheckRequested += async (_, _) => await CheckForUpdatesAtStartAsync(shiftSetup);
        desktop.MainWindow = shiftSetup;
        if (show) shiftSetup.Show();
    }

    private static async Task CheckForUpdatesAtStartAsync(ShiftSetupWindow shiftSetup)
    {
        shiftSetup.SetUpdateStatus("Checking for updates...");
        var result = await UpdateService.CheckForUpdatesAsync();
        shiftSetup.SetUpdateStatus(result.StatusText);
        if (result.Status != AppUpdateStatus.Available || !shiftSetup.IsVisible || shiftSetup.IsOpeningSession || _activeSession is not null)
        {
            return;
        }

        var releaseNotes = result.ReleaseNotes?.Trim();
        if (releaseNotes?.Length > 500)
        {
            releaseNotes = $"{releaseNotes[..500].TrimEnd()}…";
        }

        var notes = string.IsNullOrWhiteSpace(releaseNotes) ? string.Empty : $"\n\n{releaseNotes}";
        var confirmed = await new MessageWindow(
            "Update available",
            $"TCM+ {result.Version} is ready. Updating will download the release and restart TCM+.{notes}",
            true,
            "Update & restart",
            "Later").ShowDialog<bool>(shiftSetup);
        if (!confirmed)
        {
            return;
        }

        shiftSetup.SetUpdateStatus($"Downloading TCM+ {result.Version}...");
        var applyResult = await UpdateService.DownloadAndRestartAsync();
        shiftSetup.SetUpdateStatus(applyResult.StatusText);
    }

    private static async Task OpenShiftAsync(IClassicDesktopStyleApplicationLifetime desktop, ShiftSetupWindow shiftSetup, ShiftSetupDraft draft)
    {
        if (!TryAcquireHost())
        {
            shiftSetup.ShowError("Another authoritative TCM+ host is already running. Connect this app as a terminal instead.");
            return;
        }

        try
        {
            var session = await SessionStore.CreateAsync(draft.ShiftName, draft.SessionPassword);
            await ConfigureServicesAsync(session, draft);
            await SessionStore.SealAsync(session, draft.SessionPassword);
            session = await SessionStore.OpenAsync((await SessionStore.GetRecentAsync()).Single(item => item.Id == session.Id), draft.SessionPassword);
            var services = await ConfigureServicesAsync(session, null);
            ShowSessionWindow(desktop, session, draft.SessionPassword, services);
            shiftSetup.Close();
        }
        catch (Exception exception)
        {
            ReleaseHostIfInactive();
            shiftSetup.ShowError($"Unable to start this shift: {exception.Message}");
        }
    }

    public static async void ShowRecentSessions(Avalonia.Controls.Window owner)
    {
        var request = await new RecentSessionsWindow(SessionStore).ShowDialog<SessionOpenRequest?>(owner);
        if (request is null || _desktop is null) return;
        try
        {
            if (_activeSession?.Session.Id == request.Entry.Id)
            {
                return;
            }

            if (_activeSession is null && !TryAcquireHost())
            {
                throw new InvalidOperationException("Another authoritative TCM+ host is already running. Connect this app as a terminal instead.");
            }

            var session = await SessionStore.OpenAsync(request.Entry, request.Password);
            var services = await ConfigureServicesAsync(session, null);
            var settings = services.GetRequiredService<ITcSettingsRepository>();
            var current = await settings.GetAsync();
            await settings.SaveAsync(current with { ShiftName = request.Entry.ShiftName });

            if (_activeSession is not null)
            {
                await CloseActiveSessionAsync(_activeSession, releaseHostMutex: false);
            }

            ShowSessionWindow(_desktop, session, request.Password, services);
            if (owner is ShiftSetupWindow) owner.Close();
        }
        catch (Exception exception)
        {
            ReleaseHostIfInactive();
            await new MessageWindow("Unable to load shift", exception.Message).ShowDialog(owner);
        }
    }

    private static void ShowTerminalConnection(Avalonia.Controls.Window owner)
    {
        var window = new TerminalConnectWindow();
        window.ConnectionRequested += async (_, draft) => await ConnectTerminalAsync(owner, window, draft);
        _ = window.ShowDialog(owner);
    }

    private static async Task ConnectTerminalAsync(
        Avalonia.Controls.Window owner,
        TerminalConnectWindow connectionWindow,
        TerminalConnectionDraft draft)
    {
        ITerminalApiClient? apiClient = null;
        EncryptedTerminalCommandQueue? queue = null;
        RemoteTreatmentCentreService? remoteService = null;
        try
        {
            apiClient = new TerminalApiClient(draft.Host, draft.TerminalName, draft.Password, draft.CertificateFingerprint);
            queue = new EncryptedTerminalCommandQueue(draft.Host, draft.TerminalName);
            remoteService = new RemoteTreatmentCentreService(apiClient, queue);
            var login = await remoteService.ConnectAsync();
            var session = new SessionDescriptor(
                login.TerminalId,
                DateTimeOffset.UtcNow,
                login.ShiftName,
                string.Empty,
                string.Empty);
            var services = ConfigureTerminalServices(session, draft, remoteService);
            if (_desktop is null)
            {
                throw new InvalidOperationException("The desktop application is not available.");
            }

            ShowSessionWindow(_desktop, session, string.Empty, services);
            connectionWindow.Close();
            owner.Close();
            apiClient = null;
            queue = null;
            remoteService = null;
        }
        catch (Exception exception)
        {
            remoteService?.Dispose();
            if (remoteService is null)
            {
                queue?.Dispose();
                apiClient?.Dispose();
            }
            connectionWindow.ShowError($"Unable to connect this terminal: {exception.Message}");
        }
    }

    public static async Task SealActiveSessionAsync()
    {
        var activeSession = _activeSession;
        if (activeSession is not null && !activeSession.Runtime.IsTerminal)
        {
            await StopAutosaveAsync(activeSession);
            try
            {
                await activeSession.ViewModel.StopLanDisplayForSessionAsync();
                await SessionStore.SealAsync(activeSession.Session, activeSession.Password);
                activeSession.IsSealed = true;
            }
            catch
            {
                StartAutosave(activeSession);
                throw;
            }
        }
    }

    public static async Task UnsealActiveSessionAsync()
    {
        var activeSession = _activeSession;
        if (activeSession is null || activeSession.Runtime.IsTerminal) return;
        var entry = (await SessionStore.GetRecentAsync()).Single(item => item.Id == activeSession.Session.Id);
        await SessionStore.OpenAsync(entry, activeSession.Password);
        activeSession.IsSealed = false;
        StartAutosave(activeSession);
    }

    private static void ShowSessionWindow(IClassicDesktopStyleApplicationLifetime desktop, TCMPlus.Domain.Models.SessionDescriptor session, string password, ServiceProvider services)
    {
        var viewModel = services.GetRequiredService<MainViewModel>();
        var runtime = services.GetRequiredService<TerminalRuntimeContext>();
        var window = new MainWindow { DataContext = viewModel };
        var activeSession = new ActiveSession(session, password, window, viewModel, runtime, services);
        _activeSession = activeSession;
        if (!runtime.IsTerminal)
        {
            StartAutosave(activeSession);
        }
        window.Opened += async (_, _) => await viewModel.InitializeAsync();
        window.Closing += async (_, args) =>
        {
            if (activeSession.CloseAllowed) return;
            args.Cancel = true;
            if (activeSession.CloseInProgress) return;

            activeSession.CloseInProgress = true;
            try
            {
                await CloseActiveSessionAsync(activeSession);
            }
            catch (Exception exception)
            {
                activeSession.CloseInProgress = false;
                viewModel.ReportPersistenceFailure($"Unable to safely close the shift: {exception.Message}");
            }
        };
        desktop.MainWindow = window;
        window.Show();
    }

    private static void StartAutosave(ActiveSession activeSession)
    {
        if (activeSession.Runtime.IsTerminal || activeSession.IsSealed || activeSession.AutosaveTask is not null)
        {
            return;
        }

        activeSession.AutosaveCancellation = new CancellationTokenSource();
        activeSession.AutosaveTask = RunAutosaveAsync(activeSession, activeSession.AutosaveCancellation.Token);
    }

    private static async Task RunAutosaveAsync(ActiveSession activeSession, CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(AutosaveInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                if (!ReferenceEquals(_activeSession, activeSession) || activeSession.IsSealed)
                {
                    return;
                }

                try
                {
                    await SessionStore.AutosaveAsync(activeSession.Session, activeSession.Password, cancellationToken);
                    activeSession.AutosaveErrorReported = false;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception exception)
                {
                    if (!activeSession.AutosaveErrorReported)
                    {
                        activeSession.AutosaveErrorReported = true;
                        Dispatcher.UIThread.Post(() =>
                            activeSession.ViewModel.ReportPersistenceFailure($"Autosave could not update the encrypted shift backup: {exception.Message}"));
                    }
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private static async Task StopAutosaveAsync(ActiveSession activeSession)
    {
        var cancellation = activeSession.AutosaveCancellation;
        var task = activeSession.AutosaveTask;
        activeSession.AutosaveCancellation = null;
        activeSession.AutosaveTask = null;
        if (cancellation is null)
        {
            return;
        }

        await cancellation.CancelAsync();
        if (task is not null)
        {
            await task;
        }

        cancellation.Dispose();
    }

    private static async Task CloseActiveSessionAsync(ActiveSession activeSession, bool releaseHostMutex = true)
    {
        await StopAutosaveAsync(activeSession);
        try
        {
            await activeSession.ViewModel.StopLanDisplayForSessionAsync();
            if (activeSession.Runtime.RemoteService is not null)
            {
                try
                {
                    await activeSession.Runtime.RemoteService.DisconnectAsync();
                }
                catch
                {
                    // Closing remains safe when the host is already unavailable.
                }
            }
            if (activeSession.Runtime.HostServer is not null)
            {
                await activeSession.Runtime.HostServer.StopAsync();
            }

            if (!activeSession.Runtime.IsTerminal && !activeSession.IsSealed)
            {
                await SessionStore.SealAsync(activeSession.Session, activeSession.Password);
                activeSession.IsSealed = true;
            }

            activeSession.ViewModel.StopUiTimersForSession();
            activeSession.CloseAllowed = true;
            if (ReferenceEquals(_activeSession, activeSession))
            {
                _activeSession = null;
            }

            activeSession.Window.Close();
            activeSession.Services.Dispose();
            if (!activeSession.Runtime.IsTerminal && releaseHostMutex)
            {
                ReleaseHostMutex();
            }
        }
        catch
        {
            StartAutosave(activeSession);
            throw;
        }
    }

    private static async Task<ServiceProvider> ConfigureServicesAsync(TCMPlus.Domain.Models.SessionDescriptor session, ShiftSetupDraft? draft)
    {
        var services = new ServiceCollection();
        var connectionFactory = new SqliteConnectionFactory(session.DatabasePath);
        await new DatabaseInitializer(connectionFactory).InitializeAsync();

        services.AddSingleton(session);
        services.AddSingleton<IAppUpdateService>(UpdateService);
        services.AddSingleton(connectionFactory);
        services.AddSingleton<IStationRepository, SqliteStationRepository>();
        services.AddSingleton<IMobileTeamRepository, SqliteMobileTeamRepository>();
        services.AddSingleton<IPatientRepository, SqlitePatientRepository>();
        services.AddSingleton<ITcSettingsRepository, SqliteTcSettingsRepository>();
        services.AddSingleton<IAppSettingsRepository, JsonAppSettingsRepository>();
        services.AddSingleton<IShiftPinService, ShiftPinService>();
        services.AddSingleton<TreatmentCentreService>();
        services.AddSingleton<SerializedTreatmentCentreService>(provider =>
            new SerializedTreatmentCentreService(provider.GetRequiredService<TreatmentCentreService>()));
        services.AddSingleton<ITreatmentCentreService>(provider =>
            provider.GetRequiredService<SerializedTreatmentCentreService>());
        services.AddSingleton<TerminalSecurityStore>();
        services.AddSingleton<TerminalCommandExecutor>();
        services.AddSingleton<TerminalHostServer>();
        services.AddSingleton<TerminalRuntimeContext>(provider =>
            TerminalRuntimeContext.Host(provider.GetRequiredService<TerminalHostServer>()));
        services.AddSingleton<LanDisplaySnapshotProvider>();
        services.AddSingleton<LanDisplayServer>();
        services.AddSingleton<MainViewModel>();
        var provider = services.BuildServiceProvider();
        if (draft is not null)
        {
            var pinService = provider.GetRequiredService<IShiftPinService>();
            var settings = pinService.CreateSettings(draft.Pin) with { ShiftName = draft.ShiftName.Trim(), GridDensity = draft.GridDensity };
            await provider.GetRequiredService<ITcSettingsRepository>().SaveAsync(settings);
        }
        return provider;
    }

    private static ServiceProvider ConfigureTerminalServices(
        SessionDescriptor session,
        TerminalConnectionDraft draft,
        RemoteTreatmentCentreService remoteService)
    {
        var services = new ServiceCollection();
        services.AddSingleton(session);
        services.AddSingleton(remoteService);
        services.AddSingleton<ITreatmentCentreService>(remoteService);
        services.AddSingleton<ITcSettingsRepository>(new RemoteTcSettingsRepository(remoteService));
        services.AddSingleton<IAppSettingsRepository>(new RemoteAppSettingsRepository(remoteService));
        services.AddSingleton<IShiftPinService, ShiftPinService>();
        services.AddSingleton(TerminalRuntimeContext.Terminal(
            remoteService,
            draft.TerminalName,
            draft.Host.GetLeftPart(UriPartial.Authority)));
        services.AddSingleton<LanDisplaySnapshotProvider>();
        services.AddSingleton<LanDisplayServer>();
        services.AddSingleton<MainViewModel>();
        return services.BuildServiceProvider();
    }

    private static bool TryAcquireHost()
    {
        if (_hostMutexOwned)
        {
            return true;
        }

        _hostMutex ??= new Mutex(false, "Local\\TCMPlus.AuthoritativeHost");
        try
        {
            _hostMutexOwned = _hostMutex.WaitOne(0);
        }
        catch (AbandonedMutexException)
        {
            _hostMutexOwned = true;
        }

        return _hostMutexOwned;
    }

    private static void ReleaseHostIfInactive()
    {
        if (_activeSession is null)
        {
            ReleaseHostMutex();
        }
    }

    private static void ReleaseHostMutex()
    {
        if (!_hostMutexOwned || _hostMutex is null)
        {
            return;
        }

        _hostMutex.ReleaseMutex();
        _hostMutexOwned = false;
    }

    private sealed class ActiveSession(
        TCMPlus.Domain.Models.SessionDescriptor session,
        string password,
        MainWindow window,
        MainViewModel viewModel,
        TerminalRuntimeContext runtime,
        ServiceProvider services)
    {
        public TCMPlus.Domain.Models.SessionDescriptor Session { get; } = session;
        public string Password { get; } = password;
        public MainWindow Window { get; } = window;
        public MainViewModel ViewModel { get; } = viewModel;
        public TerminalRuntimeContext Runtime { get; } = runtime;
        public ServiceProvider Services { get; } = services;
        public CancellationTokenSource? AutosaveCancellation { get; set; }
        public Task? AutosaveTask { get; set; }
        public bool AutosaveErrorReported { get; set; }
        public bool IsSealed { get; set; }
        public bool CloseAllowed { get; set; }
        public bool CloseInProgress { get; set; }
    }
}
