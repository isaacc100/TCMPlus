using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Microsoft.Extensions.DependencyInjection;
using TCMPlus.Domain.Persistence;
using TCMPlus.Domain.Services;
using TCMPlus.Infrastructure.Persistence;
using TCMPlus.Infrastructure.Services;
using TCMPlus.Infrastructure.Sessions;
using TCMPlus.App.ViewModels;
using TCMPlus.App.Views;

namespace TCMPlus.App;

public partial class App : Application
{
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
            var shiftSetup = new ShiftSetupWindow();
            shiftSetup.ShiftStarted += async (_, draft) => await OpenShiftAsync(desktop, shiftSetup, draft);
            shiftSetup.LoadExistingRequested += (_, _) => ShowRecentSessions(shiftSetup);
            desktop.MainWindow = shiftSetup;
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static async Task OpenShiftAsync(IClassicDesktopStyleApplicationLifetime desktop, ShiftSetupWindow shiftSetup, ShiftSetupDraft draft)
    {
        var session = await SessionStore.CreateAsync(draft.ShiftName, draft.SessionPassword);
        var services = await ConfigureServicesAsync(session, draft);
        ShowSessionWindow(desktop, session, draft.SessionPassword, services);
        shiftSetup.Close();
    }

    public static async void ShowRecentSessions(Avalonia.Controls.Window owner)
    {
        var request = await new RecentSessionsWindow(SessionStore).ShowDialog<SessionOpenRequest?>(owner);
        if (request is null || _desktop is null) return;
        try
        {
            if (_activeSession is not null)
            {
                await SessionStore.SealAsync(_activeSession.Session, _activeSession.Password);
                _activeSession.Window.Close();
            }
            var session = await SessionStore.OpenAsync(request.Entry, request.Password);
            var services = await ConfigureServicesAsync(session, null);
            var settings = services.GetRequiredService<ITcSettingsRepository>();
            var current = await settings.GetAsync();
            await settings.SaveAsync(current with { ShiftName = request.Entry.ShiftName });
            ShowSessionWindow(_desktop, session, request.Password, services);
        }
        catch (Exception exception)
        {
            await new MessageWindow("Unable to load shift", exception.Message).ShowDialog(owner);
        }
    }

    private static void ShowSessionWindow(IClassicDesktopStyleApplicationLifetime desktop, TCMPlus.Domain.Models.SessionDescriptor session, string password, ServiceProvider services)
    {
        var viewModel = services.GetRequiredService<MainViewModel>();
        var window = new MainWindow { DataContext = viewModel };
        _activeSession = new ActiveSession(session, password, window);
        window.Opened += async (_, _) => await viewModel.InitializeAsync();
        window.Closing += async (_, _) => await SessionStore.SealAsync(session, password);
        desktop.MainWindow = window;
        window.Show();
    }

    private static async Task<ServiceProvider> ConfigureServicesAsync(TCMPlus.Domain.Models.SessionDescriptor session, ShiftSetupDraft? draft)
    {
        var services = new ServiceCollection();
        var connectionFactory = new SqliteConnectionFactory(session.DatabasePath);
        await new DatabaseInitializer(connectionFactory).InitializeAsync();

        services.AddSingleton(session);
        services.AddSingleton(connectionFactory);
        services.AddSingleton<IStationRepository, SqliteStationRepository>();
        services.AddSingleton<IPatientRepository, SqlitePatientRepository>();
        services.AddSingleton<ITcSettingsRepository, SqliteTcSettingsRepository>();
        services.AddSingleton<IAppSettingsRepository, JsonAppSettingsRepository>();
        services.AddSingleton<IShiftPinService, ShiftPinService>();
        services.AddSingleton<ITreatmentCentreService, TreatmentCentreService>();
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

    private sealed record ActiveSession(TCMPlus.Domain.Models.SessionDescriptor Session, string Password, MainWindow Window);
}
