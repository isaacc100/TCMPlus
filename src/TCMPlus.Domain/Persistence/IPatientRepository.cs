using TCMPlus.Domain.Models;

namespace TCMPlus.Domain.Persistence;

public interface IPatientRepository
{
    Task<IReadOnlyList<Patient>> GetAllActiveAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<Patient>> GetAllAsync(CancellationToken cancellationToken = default);
    Task<int> GetNextPatientNumberAsync(CancellationToken cancellationToken = default);
    Task<Patient?> GetByUidAsync(Guid patientUid, CancellationToken cancellationToken = default);
    Task<Patient?> GetByStationAsync(Guid stationId, CancellationToken cancellationToken = default);
    Task<Patient?> GetByMobileTeamAsync(Guid teamId, CancellationToken cancellationToken = default);
    Task AddAsync(Patient patient, CancellationToken cancellationToken = default);
    Task UpdateDetailsAsync(Patient patient, CancellationToken cancellationToken = default);
    Task UpdatePresentingComplaintAsync(IReadOnlyCollection<Guid> patientUids, string presentingComplaint, CancellationToken cancellationToken = default);
    Task DeleteAsync(Guid patientUid, CancellationToken cancellationToken = default);
    Task<Patient?> DischargeFromStationAsync(Guid stationId, DateTimeOffset dischargedAt, string? dischargeRoute, string? dischargeOutcome, CancellationToken cancellationToken = default);
    Task<Patient?> DischargeAsync(Guid patientUid, DateTimeOffset dischargedAt, string? dischargeRoute, string? dischargeOutcome, CancellationToken cancellationToken = default);
    Task<PatientTransferResult> MoveAsync(Guid sourceStationId, Guid destinationStationId, bool swap, CancellationToken cancellationToken = default);
    Task<PatientTransferResult> MoveAsync(Guid patientUid, PatientAssignment destination, bool swap, CancellationToken cancellationToken = default);
    Task AddEventAsync(PatientEvent patientEvent, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<PatientEvent>> GetAllEventsAsync(CancellationToken cancellationToken = default);
}
