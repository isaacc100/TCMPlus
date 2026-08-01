using TCMPlus.Domain.Models;

namespace TCMPlus.Domain.Persistence;

public interface ITreatmentCentreLayoutRepository
{
    Task CommitAsync(
        TreatmentCentreLayout layout,
        DateTimeOffset deletedAt,
        CancellationToken cancellationToken = default);
}
