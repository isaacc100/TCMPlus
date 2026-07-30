using TCMPlus.Domain.Models;
using TCMPlus.Domain.Persistence;

namespace TCMPlus.Infrastructure.Networking;

public sealed class RemoteTcSettingsRepository : ITcSettingsRepository
{
    private readonly RemoteTreatmentCentreService _service;

    public RemoteTcSettingsRepository(RemoteTreatmentCentreService service)
    {
        _service = service;
    }

    public async Task<TcSessionSettings> GetAsync(CancellationToken cancellationToken = default)
    {
        var snapshot = _service.LastSnapshot ?? await _service.RefreshAsync(cancellationToken);
        return new TcSessionSettings(
            snapshot.ShiftName,
            null,
            null,
            snapshot.QuickEntry,
            (GridDensity)(int)snapshot.GridDensity);
    }

    public Task SaveAsync(TcSessionSettings settings, CancellationToken cancellationToken = default) =>
        Task.FromException(new InvalidOperationException("Shift settings can only be changed on the authoritative host."));
}

public sealed class RemoteAppSettingsRepository(RemoteTreatmentCentreService service) : IAppSettingsRepository
{
    public async Task<AppSettings> GetAsync(CancellationToken cancellationToken = default)
    {
        var snapshot = service.LastSnapshot ?? await service.RefreshAsync(cancellationToken);
        return new AppSettings(snapshot.DischargeRoutes.Count == 0 ? AppSettings.Default.DischargeRoutes : snapshot.DischargeRoutes);
    }

    public Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default) =>
        Task.FromException(new InvalidOperationException("Application settings can only be changed on the authoritative host."));
}
