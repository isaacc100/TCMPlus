using TCMPlus.Domain.Models;

namespace TCMPlus.Domain.Persistence;

public interface IStationRepository
{
    Task<IReadOnlyList<Station>> GetAllAsync(CancellationToken cancellationToken = default);
    Task AddAsync(Station station, CancellationToken cancellationToken = default);
    Task UpdateAsync(Station station, CancellationToken cancellationToken = default);
    Task SoftDeleteAsync(Guid stationId, DateTimeOffset deletedAt, CancellationToken cancellationToken = default);
}
