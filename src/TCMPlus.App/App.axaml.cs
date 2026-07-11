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
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var shiftSetup = new ShiftSetupWindow();
            shiftSetup.ShiftStarted += (_, draft) => OpenShift(desktop, shiftSetup, draft);
            desktop.MainWindow = shiftSetup;
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static void OpenShift(IClassicDesktopStyleApplicationLifetime desktop, ShiftSetupWindow shiftSetup, ShiftSetupDraft draft)
    {
        var session = new SessionFactory().CreateNewSession(draft.ShiftName);
        var services = ConfigureServices(session, draft);
        var viewModel = services.GetRequiredService<MainViewModel>();
        var window = new MainWindow { DataContext = viewModel };
        window.Opened += async (_, _) => await viewModel.InitializeAsync();
        desktop.MainWindow = window;
        window.Show();
        shiftSetup.Close();
    }

    private static ServiceProvider ConfigureServices(TCMPlus.Domain.Models.SessionDescriptor session, ShiftSetupDraft draft)
    {
        var services = new ServiceCollection();
        var connectionFactory = new SqliteConnectionFactory(session.DatabasePath);
        new DatabaseInitializer(connectionFactory).InitializeAsync().GetAwaiter().GetResult();

        services.AddSingleton(session);
        services.AddSingleton(connectionFactory);
        services.AddSingleton<IStationRepository, SqliteStationRepository>();
        services.AddSingleton<IPatientRepository, SqlitePatientRepository>();
        services.AddSingleton<ITcSettingsRepository, SqliteTcSettingsRepository>();
        services.AddSingleton<IShiftPinService, ShiftPinService>();
        services.AddSingleton<ITreatmentCentreService, TreatmentCentreService>();
        services.AddSingleton<MainViewModel>();
        var provider = services.BuildServiceProvider();
        var pinService = provider.GetRequiredService<IShiftPinService>();
        var settings = pinService.CreateSettings(draft.Pin) with { ShiftName = draft.ShiftName.Trim() };
        provider.GetRequiredService<ITcSettingsRepository>().SaveAsync(settings).GetAwaiter().GetResult();
        return provider;
    }
}
