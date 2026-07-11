using TCMPlus.Domain.Models;

namespace TCMPlus.Domain.Persistence;

public interface IPatientRepository
{
    Task<IReadOnlyList<Patient>> GetAllActiveAsync(CancellationToken cancellationToken = default);
    Task<int> GetDischargedCountAsync(CancellationToken cancellationToken = default);
    Task<Patient?> GetByStationAsync(Guid stationId, CancellationToken cancellationToken = default);
    Task AddAsync(Patient patient, CancellationToken cancellationToken = default);
    Task DischargeFromStationAsync(Guid stationId, CancellationToken cancellationToken = default);
}
