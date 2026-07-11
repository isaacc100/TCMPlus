using TCMPlus.Domain.Models;

namespace TCMPlus.Domain.Persistence;

public interface ITcSettingsRepository
{
    Task<TcSessionSettings> GetAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(TcSessionSettings settings, CancellationToken cancellationToken = default);
}
