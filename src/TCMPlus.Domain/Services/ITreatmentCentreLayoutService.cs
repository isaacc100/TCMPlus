using TCMPlus.Domain.Models;

namespace TCMPlus.Domain.Services;

public interface ITreatmentCentreLayoutService
{
    Task<TreatmentCentreLayout> LoadAsync(CancellationToken cancellationToken = default);
    Task CommitAsync(TreatmentCentreLayout layout, CancellationToken cancellationToken = default);
}
