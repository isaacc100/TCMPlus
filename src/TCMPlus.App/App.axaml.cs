using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using System.Diagnostics;
using TCMPlus.Domain.Persistence;
using TCMPlus.Domain.Services;
using TCMPlus.Infrastructure.Persistence;
using TCMPlus.Infrastructure.Services;
using TCMPlus.Infrastructure.Sessions;
using TCMPlus.App.ViewModels;
using TCMPlus.App.Views;
using TCMPlus.App.LanDisplay;

namespace TCMPlus.App;

public partial class App : Application
{
    private static readonly TimeSpan AutosaveInterval = TimeSpan.FromSeconds(30);
    private static readonly EncryptedSessionStore SessionStore = new();
    private static IClassicDesktopStyleApplicationLifetime? _desktop;
    private static ActiveSession? _activeSession;
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            _desktop = desktop;
            var otherProcesses = FindOtherInstances();
            if (otherProcesses.Count == 0)
            {
                CreateShiftSetup(desktop, false);
            }
            else
            {
                var conflict = new ProcessConflictWindow(otherProcesses.Count);
                conflict.Resolved += async (_, terminateOthers) =>
                {
                    if (terminateOthers)
                    {
                        var failed = await TerminateAsync(otherProcesses);
                        if (failed.Count > 0) { conflict.ShowError($"Could not stop {failed.Count} existing TCM+ instance(s). Close them manually, then try again."); return; }
                    }
                    else { desktop.Shutdown(); return; }

                    conflict.Close();
                    CreateShiftSetup(desktop, true);
                };
                desktop.MainWindow = conflict;
            }
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static void CreateShiftSetup(IClassicDesktopStyleApplicationLifetime desktop, bool show)
    {
        var shiftSetup = new ShiftSetupWindow();
        shiftSetup.ShiftStarted += async (_, draft) => await OpenShiftAsync(desktop, shiftSetup, draft);
        shiftSetup.LoadExistingRequested += (_, _) => ShowRecentSessions(shiftSetup);
        desktop.MainWindow = shiftSetup;
        if (show) shiftSetup.Show();
    }

    private static List<Process> FindOtherInstances()
    {
        using var current = Process.GetCurrentProcess();
        return Process.GetProcessesByName(current.ProcessName).Where(process => process.Id != current.Id).ToList();
    }

    private static async Task<List<Process>> TerminateAsync(IEnumerable<Process> processes)
    {
        var failed = new List<Process>();
        foreach (var process in processes)
        {
            try
            {
                if (!process.HasExited) process.Kill();
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3));
            }
            catch { failed.Add(process); }
            finally { process.Dispose(); }
        }
        return failed;
    }

    private static async Task OpenShiftAsync(IClassicDesktopStyleApplicationLifetime desktop, ShiftSetupWindow shiftSetup, ShiftSetupDraft draft)
    {
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

            var session = await SessionStore.OpenAsync(request.Entry, request.Password);
            var services = await ConfigureServicesAsync(session, null);
            var settings = services.GetRequiredService<ITcSettingsRepository>();
            var current = await settings.GetAsync();
            await settings.SaveAsync(current with { ShiftName = request.Entry.ShiftName });

            if (_activeSession is not null)
            {
                await CloseActiveSessionAsync(_activeSession);
            }

            ShowSessionWindow(_desktop, session, request.Password, services);
            if (owner is ShiftSetupWindow) owner.Close();
        }
        catch (Exception exception)
        {
            await new MessageWindow("Unable to load shift", exception.Message).ShowDialog(owner);
        }
    }

    public static async Task SealActiveSessionAsync()
    {
        var activeSession = _activeSession;
        if (activeSession is not null)
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
        if (activeSession is null) return;
        var entry = (await SessionStore.GetRecentAsync()).Single(item => item.Id == activeSession.Session.Id);
        await SessionStore.OpenAsync(entry, activeSession.Password);
        activeSession.IsSealed = false;
        StartAutosave(activeSession);
    }

    private static void ShowSessionWindow(IClassicDesktopStyleApplicationLifetime desktop, TCMPlus.Domain.Models.SessionDescriptor session, string password, ServiceProvider services)
    {
        var viewModel = services.GetRequiredService<MainViewModel>();
        var window = new MainWindow { DataContext = viewModel };
        var activeSession = new ActiveSession(session, password, window, viewModel);
        _activeSession = activeSession;
        StartAutosave(activeSession);
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
        if (activeSession.IsSealed || activeSession.AutosaveTask is not null)
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

    private static async Task CloseActiveSessionAsync(ActiveSession activeSession)
    {
        await StopAutosaveAsync(activeSession);
        try
        {
            await activeSession.ViewModel.StopLanDisplayForSessionAsync();
            if (!activeSession.IsSealed)
            {
                await SessionStore.SealAsync(activeSession.Session, activeSession.Password);
                activeSession.IsSealed = true;
            }

            activeSession.CloseAllowed = true;
            if (ReferenceEquals(_activeSession, activeSession))
            {
                _activeSession = null;
            }

            activeSession.Window.Close();
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
        services.AddSingleton(connectionFactory);
        services.AddSingleton<IStationRepository, SqliteStationRepository>();
        services.AddSingleton<IMobileTeamRepository, SqliteMobileTeamRepository>();
        services.AddSingleton<IPatientRepository, SqlitePatientRepository>();
        services.AddSingleton<ITcSettingsRepository, SqliteTcSettingsRepository>();
        services.AddSingleton<IAppSettingsRepository, JsonAppSettingsRepository>();
        services.AddSingleton<IShiftPinService, ShiftPinService>();
        services.AddSingleton<ITreatmentCentreService, TreatmentCentreService>();
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

    private sealed class ActiveSession(
        TCMPlus.Domain.Models.SessionDescriptor session,
        string password,
        MainWindow window,
        MainViewModel viewModel)
    {
        public TCMPlus.Domain.Models.SessionDescriptor Session { get; } = session;
        public string Password { get; } = password;
        public MainWindow Window { get; } = window;
        public MainViewModel ViewModel { get; } = viewModel;
        public CancellationTokenSource? AutosaveCancellation { get; set; }
        public Task? AutosaveTask { get; set; }
        public bool AutosaveErrorReported { get; set; }
        public bool IsSealed { get; set; }
        public bool CloseAllowed { get; set; }
        public bool CloseInProgress { get; set; }
    }
}
