using TCMPlus.Domain.Models;

namespace TCMPlus.Domain.Persistence;

public interface IMobileTeamRepository
{
    Task<IReadOnlyList<MobileTeam>> GetAllAsync(CancellationToken cancellationToken = default);
    Task<MobileTeam?> GetByIdAsync(Guid teamId, CancellationToken cancellationToken = default);
    Task AddAsync(MobileTeam team, CancellationToken cancellationToken = default);
    Task UpdateAsync(MobileTeam team, CancellationToken cancellationToken = default);
    Task SoftDeleteAsync(Guid teamId, DateTimeOffset deletedAt, CancellationToken cancellationToken = default);
}
