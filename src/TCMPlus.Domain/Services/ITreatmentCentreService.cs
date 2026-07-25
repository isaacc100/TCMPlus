using TCMPlus.Domain.Models;

namespace TCMPlus.Domain.Services;

public interface ITreatmentCentreService
{
    Task<IReadOnlyList<StationSnapshot>> GetSnapshotAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<MobileTeamSnapshot>> GetMobileTeamsAsync(CancellationToken cancellationToken = default);
    Task<Station> AddStationAsync(string name, string type, CancellationToken cancellationToken = default);
    Task SaveStationAsync(Station station, CancellationToken cancellationToken = default);
    Task ReorderStationsAsync(IReadOnlyList<Guid> stationIds, CancellationToken cancellationToken = default);
    Task DeleteStationAsync(Guid stationId, CancellationToken cancellationToken = default);
    Task<MobileTeam> AddMobileTeamAsync(string callsign, string? note, CancellationToken cancellationToken = default);
    Task<MobileTeam> UpdateMobileTeamAsync(Guid teamId, string callsign, string? note, CancellationToken cancellationToken = default);
    Task DeleteMobileTeamAsync(Guid teamId, CancellationToken cancellationToken = default);
    Task<MobileTeam> DeployMobileTeamAsync(Guid teamId, string? location, CancellationToken cancellationToken = default);
    Task<MobileTeam> UpdateMobileTeamLocationAsync(Guid teamId, string? location, CancellationToken cancellationToken = default);
    Task<MobileTeam> StandDownMobileTeamAsync(Guid teamId, CancellationToken cancellationToken = default);
    Task<Patient> AddPatientAsync(Guid stationId, string? presentingComplaint, CancellationToken cancellationToken = default);
    Task<Patient> AddPatientToMobileTeamAsync(Guid teamId, string? presentingComplaint, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<Patient>> GetPatientsAsync(CancellationToken cancellationToken = default);
    Task<Patient> UpdatePatientDetailsAsync(Guid patientUid, DateTimeOffset addedAt, DateTimeOffset? dischargedAt, string? presentingComplaint, string? dischargeRoute, string? dischargeOutcome, CancellationToken cancellationToken = default);
    Task UpdatePresentingComplaintAsync(IReadOnlyCollection<Guid> patientUids, string presentingComplaint, CancellationToken cancellationToken = default);
    Task DeletePatientAsync(Guid patientUid, CancellationToken cancellationToken = default);
    Task DischargePatientAsync(Guid stationId, string? dischargeRoute, string? dischargeOutcome, CancellationToken cancellationToken = default);
    Task DischargeAssignedPatientAsync(Guid patientUid, string? dischargeRoute, string? dischargeOutcome, CancellationToken cancellationToken = default);
    Task<PatientTransferResult> MovePatientAsync(Guid sourceStationId, Guid destinationStationId, bool swap, CancellationToken cancellationToken = default);
    Task<PatientTransferResult> MovePatientAsync(Guid patientUid, PatientAssignment destination, bool swap, CancellationToken cancellationToken = default);
    Task<int> GetPatientsSeenThisShiftAsync(CancellationToken cancellationToken = default);
    Task<DashboardSnapshot> GetDashboardAsync(CancellationToken cancellationToken = default);
}
