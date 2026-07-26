using TCMPlus.Domain.Models;
using TCMPlus.Domain.Services;

namespace TCMPlus.Infrastructure.Networking;

public sealed class SerializedTreatmentCentreService(ITreatmentCentreService inner) : ITreatmentCentreService
{
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private int _hostWaiters;

    public Task<IReadOnlyList<StationSnapshot>> GetSnapshotAsync(CancellationToken cancellationToken = default) =>
        inner.GetSnapshotAsync(cancellationToken);

    public Task<IReadOnlyList<MobileTeamSnapshot>> GetMobileTeamsAsync(CancellationToken cancellationToken = default) =>
        inner.GetMobileTeamsAsync(cancellationToken);

    public Task<IReadOnlyList<Patient>> GetPatientsAsync(CancellationToken cancellationToken = default) =>
        inner.GetPatientsAsync(cancellationToken);

    public Task<int> GetPatientsSeenThisShiftAsync(CancellationToken cancellationToken = default) =>
        inner.GetPatientsSeenThisShiftAsync(cancellationToken);

    public Task<DashboardSnapshot> GetDashboardAsync(CancellationToken cancellationToken = default) =>
        inner.GetDashboardAsync(cancellationToken);

    public Task<Station> AddStationAsync(string name, string type, CancellationToken cancellationToken = default) =>
        ExecuteHostAsync(service => service.AddStationAsync(name, type, cancellationToken), cancellationToken);

    public Task SaveStationAsync(Station station, CancellationToken cancellationToken = default) =>
        ExecuteHostAsync(service => service.SaveStationAsync(station, cancellationToken), cancellationToken);

    public Task ReorderStationsAsync(IReadOnlyList<Guid> stationIds, CancellationToken cancellationToken = default) =>
        ExecuteHostAsync(service => service.ReorderStationsAsync(stationIds, cancellationToken), cancellationToken);

    public Task DeleteStationAsync(Guid stationId, CancellationToken cancellationToken = default) =>
        ExecuteHostAsync(service => service.DeleteStationAsync(stationId, cancellationToken), cancellationToken);

    public Task<MobileTeam> AddMobileTeamAsync(string callsign, string? note, CancellationToken cancellationToken = default) =>
        ExecuteHostAsync(service => service.AddMobileTeamAsync(callsign, note, cancellationToken), cancellationToken);

    public Task<MobileTeam> UpdateMobileTeamAsync(Guid teamId, string callsign, string? note, CancellationToken cancellationToken = default) =>
        ExecuteHostAsync(service => service.UpdateMobileTeamAsync(teamId, callsign, note, cancellationToken), cancellationToken);

    public Task DeleteMobileTeamAsync(Guid teamId, CancellationToken cancellationToken = default) =>
        ExecuteHostAsync(service => service.DeleteMobileTeamAsync(teamId, cancellationToken), cancellationToken);

    public Task<MobileTeam> DeployMobileTeamAsync(Guid teamId, string? location, CancellationToken cancellationToken = default) =>
        ExecuteHostAsync(service => service.DeployMobileTeamAsync(teamId, location, cancellationToken), cancellationToken);

    public Task<MobileTeam> UpdateMobileTeamLocationAsync(Guid teamId, string? location, CancellationToken cancellationToken = default) =>
        ExecuteHostAsync(service => service.UpdateMobileTeamLocationAsync(teamId, location, cancellationToken), cancellationToken);

    public Task<MobileTeam> StandDownMobileTeamAsync(Guid teamId, CancellationToken cancellationToken = default) =>
        ExecuteHostAsync(service => service.StandDownMobileTeamAsync(teamId, cancellationToken), cancellationToken);

    public Task<Patient> AddPatientAsync(Guid stationId, string? presentingComplaint, CancellationToken cancellationToken = default) =>
        ExecuteHostAsync(service => service.AddPatientAsync(stationId, presentingComplaint, cancellationToken), cancellationToken);

    public Task<Patient> AddPatientToMobileTeamAsync(Guid teamId, string? presentingComplaint, CancellationToken cancellationToken = default) =>
        ExecuteHostAsync(service => service.AddPatientToMobileTeamAsync(teamId, presentingComplaint, cancellationToken), cancellationToken);

    public Task<Patient> UpdatePatientDetailsAsync(Guid patientUid, DateTimeOffset addedAt, DateTimeOffset? dischargedAt, string? presentingComplaint, string? dischargeRoute, string? dischargeOutcome, CancellationToken cancellationToken = default) =>
        ExecuteHostAsync(service => service.UpdatePatientDetailsAsync(patientUid, addedAt, dischargedAt, presentingComplaint, dischargeRoute, dischargeOutcome, cancellationToken), cancellationToken);

    public Task UpdatePresentingComplaintAsync(IReadOnlyCollection<Guid> patientUids, string presentingComplaint, CancellationToken cancellationToken = default) =>
        ExecuteHostAsync(service => service.UpdatePresentingComplaintAsync(patientUids, presentingComplaint, cancellationToken), cancellationToken);

    public Task DeletePatientAsync(Guid patientUid, CancellationToken cancellationToken = default) =>
        ExecuteHostAsync(service => service.DeletePatientAsync(patientUid, cancellationToken), cancellationToken);

    public Task DischargePatientAsync(Guid stationId, string? dischargeRoute, string? dischargeOutcome, CancellationToken cancellationToken = default) =>
        ExecuteHostAsync(service => service.DischargePatientAsync(stationId, dischargeRoute, dischargeOutcome, cancellationToken), cancellationToken);

    public Task DischargeAssignedPatientAsync(Guid patientUid, string? dischargeRoute, string? dischargeOutcome, CancellationToken cancellationToken = default) =>
        ExecuteHostAsync(service => service.DischargeAssignedPatientAsync(patientUid, dischargeRoute, dischargeOutcome, cancellationToken), cancellationToken);

    public Task<PatientTransferResult> MovePatientAsync(Guid sourceStationId, Guid destinationStationId, bool swap, CancellationToken cancellationToken = default) =>
        ExecuteHostAsync(service => service.MovePatientAsync(sourceStationId, destinationStationId, swap, cancellationToken), cancellationToken);

    public Task<PatientTransferResult> MovePatientAsync(Guid patientUid, PatientAssignment destination, bool swap, CancellationToken cancellationToken = default) =>
        ExecuteHostAsync(service => service.MovePatientAsync(patientUid, destination, swap, cancellationToken), cancellationToken);

    public async Task<T> ExecuteRemoteAsync<T>(
        Func<ITreatmentCentreService, Task<T>> operation,
        CancellationToken cancellationToken = default)
    {
        while (Volatile.Read(ref _hostWaiters) > 0)
        {
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
        }

        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            return await operation(inner);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async Task ExecuteRemoteAsync(
        Func<ITreatmentCentreService, Task> operation,
        CancellationToken cancellationToken = default)
    {
        await ExecuteRemoteAsync(async service =>
        {
            await operation(service);
            return true;
        }, cancellationToken);
    }

    private async Task<T> ExecuteHostAsync<T>(
        Func<ITreatmentCentreService, Task<T>> operation,
        CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _hostWaiters);
        try
        {
            await _writeGate.WaitAsync(cancellationToken);
        }
        finally
        {
            Interlocked.Decrement(ref _hostWaiters);
        }

        try
        {
            return await operation(inner);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private Task ExecuteHostAsync(
        Func<ITreatmentCentreService, Task> operation,
        CancellationToken cancellationToken) =>
        ExecuteHostAsync(async service =>
        {
            await operation(service);
            return true;
        }, cancellationToken);
}
