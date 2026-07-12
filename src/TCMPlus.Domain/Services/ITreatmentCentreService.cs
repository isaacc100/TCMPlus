using TCMPlus.Domain.Models;

namespace TCMPlus.Domain.Services;

public interface ITreatmentCentreService
{
    Task<IReadOnlyList<StationSnapshot>> GetSnapshotAsync(CancellationToken cancellationToken = default);
    Task<Station> AddStationAsync(string name, string type, CancellationToken cancellationToken = default);
    Task SaveStationAsync(Station station, CancellationToken cancellationToken = default);
    Task DeleteStationAsync(Guid stationId, CancellationToken cancellationToken = default);
    Task<Patient> AddPatientAsync(Guid stationId, string? presentingComplaint, CancellationToken cancellationToken = default);
    Task DischargePatientAsync(Guid stationId, CancellationToken cancellationToken = default);
    Task<PatientTransferResult> MovePatientAsync(Guid sourceStationId, Guid destinationStationId, bool swap, CancellationToken cancellationToken = default);
    Task<int> GetPatientsSeenThisShiftAsync(CancellationToken cancellationToken = default);
    Task<DashboardSnapshot> GetDashboardAsync(CancellationToken cancellationToken = default);
}
