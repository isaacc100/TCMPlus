using Microsoft.Extensions.DependencyInjection;
using TCMPlus.App.LanDisplay;
using TCMPlus.App.Updates;
using TCMPlus.App.ViewModels;
using TCMPlus.App.Views;
using TCMPlus.Domain.Models;
using TCMPlus.Domain.Persistence;
using TCMPlus.Domain.Services;
using TCMPlus.Infrastructure.Networking;
using TCMPlus.Infrastructure.Persistence;
using TCMPlus.Infrastructure.Services;

namespace TCMPlus.App.TerminalNetworking;

internal static class TerminalServiceProviderFactory
{
    public static ServiceProvider Create(
        SessionDescriptor session,
        TerminalConnectionDraft draft,
        RemoteTreatmentCentreService remoteService,
        IAppUpdateService updateService)
    {
        var services = new ServiceCollection();
        services.AddSingleton(session);
        services.AddSingleton(updateService);
        services.AddSingleton(remoteService);
        services.AddSingleton<TerminalOperatorPreferencesStore>();
        services.AddSingleton<DevicePreferencesStore>();
        services.AddSingleton<ITreatmentCentreService>(remoteService);
        services.AddSingleton<ITreatmentCentreLayoutService, TerminalReadOnlyLayoutService>();
        services.AddSingleton<ITcSettingsRepository>(new RemoteTcSettingsRepository(remoteService));
        services.AddSingleton<IAppSettingsRepository>(new RemoteAppSettingsRepository(remoteService));
        services.AddSingleton<IShiftPinService, ShiftPinService>();
        services.AddSingleton(TerminalRuntimeContext.Terminal(
            remoteService,
            draft.TerminalName,
            draft.Host.GetLeftPart(UriPartial.Authority),
            draft.HostInstanceId));
        services.AddSingleton<LanDisplaySnapshotProvider>();
        services.AddSingleton<LanDisplayServer>();
        services.AddSingleton<MainViewModel>();
        return services.BuildServiceProvider();
    }
}

internal sealed class TerminalReadOnlyLayoutService(ITreatmentCentreService treatmentCentreService, ITcSettingsRepository settingsRepository)
    : ITreatmentCentreLayoutService
{
    public async Task<TreatmentCentreLayout> LoadAsync(CancellationToken cancellationToken = default)
    {
        var snapshot = await treatmentCentreService.GetSnapshotAsync(cancellationToken);
        var settings = await settingsRepository.GetAsync(cancellationToken);
        return new TreatmentCentreLayout(snapshot.Select(item => item.Station).ToList(), settings.GridDensity);
    }

    public Task CommitAsync(TreatmentCentreLayout layout, CancellationToken cancellationToken = default) =>
        throw new UnauthorizedAccessException("Treatment Centre layouts can only be edited on the host.");
}
