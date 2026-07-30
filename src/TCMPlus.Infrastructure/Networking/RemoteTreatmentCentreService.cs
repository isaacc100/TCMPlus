using TCMPlus.Domain.Models;
using TCMPlus.Domain.Persistence;
using TCMPlus.Domain.Services;
using TCMPlus.Infrastructure.Services;
using TCMPlus.Protocol;

namespace TCMPlus.Infrastructure.Networking;

public sealed class RemoteTreatmentCentreService(
    ITerminalApiClient apiClient,
    EncryptedTerminalCommandQueue queue) : ITreatmentCentreService, IDisposable
{
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private TerminalSnapshotResponse? _snapshot;

    public event EventHandler? QueueChanged;

    public int PendingCommandCount => queue.PendingCount;
    public int RejectedCommandCount => queue.RejectedCount;
    public int UnresolvedCommandCount => queue.UnresolvedCount;
    public TerminalSnapshotResponse? LastSnapshot => _snapshot;

    public async Task<TerminalLoginResponse> ConnectAsync(CancellationToken cancellationToken = default)
    {
        var login = await apiClient.AuthenticateAsync(cancellationToken);
        await RefreshAsync(cancellationToken);
        return login;
    }

    public Task DisconnectAsync(CancellationToken cancellationToken = default) =>
        apiClient.DisconnectAsync(cancellationToken);

    public async Task<IReadOnlyList<StationSnapshot>> GetSnapshotAsync(CancellationToken cancellationToken = default)
    {
        var snapshot = await RefreshAsync(cancellationToken);
        return snapshot.Stations.Select(station => new StationSnapshot(
            new Station(station.Id, station.Name, station.Type, station.GridX, station.GridY, station.GridWidth, station.GridHeight),
            ToPatient(station.Patient, station.Id, null))).ToList();
    }

    public async Task<IReadOnlyList<MobileTeamSnapshot>> GetMobileTeamsAsync(CancellationToken cancellationToken = default)
    {
        var snapshot = await CurrentOrRefreshAsync(cancellationToken);
        return snapshot.MobileTeams.Select(team => new MobileTeamSnapshot(
            new MobileTeam(team.Id, team.Callsign, team.Note, team.IsDeployed, team.DeploymentLocation),
            ToPatient(team.Patient, null, team.Id))).ToList();
    }

    public Task<Station> AddStationAsync(string name, string type, CancellationToken cancellationToken = default) =>
        HostOnlyAsync<Station>();

    public Task SaveStationAsync(Station station, CancellationToken cancellationToken = default) =>
        HostOnlyAsync();

    public Task ReorderStationsAsync(IReadOnlyList<Guid> stationIds, CancellationToken cancellationToken = default) =>
        HostOnlyAsync();

    public Task DeleteStationAsync(Guid stationId, CancellationToken cancellationToken = default) =>
        HostOnlyAsync();

    public async Task<MobileTeam> AddMobileTeamAsync(string callsign, string? note, CancellationToken cancellationToken = default)
    {
        await SendCommandAsync(NewCommand(TerminalCommandKind.AddMobileTeam) with { Name = callsign, Note = note }, cancellationToken);
        var snapshot = await RefreshAsync(cancellationToken);
        return snapshot.MobileTeams.Single(team => string.Equals(team.Callsign, callsign.Trim(), StringComparison.OrdinalIgnoreCase)) is { } created
            ? ToMobileTeam(created)
            : throw new InvalidOperationException("The host accepted the mobile team but it was not present after refresh.");
    }

    public async Task<MobileTeam> UpdateMobileTeamAsync(Guid teamId, string callsign, string? note, CancellationToken cancellationToken = default)
    {
        await SendCommandAsync(NewCommand(TerminalCommandKind.UpdateMobileTeam) with { TargetId = teamId, Name = callsign, Note = note }, cancellationToken);
        return ToMobileTeam((await RefreshAsync(cancellationToken)).MobileTeams.Single(team => team.Id == teamId));
    }

    public Task DeleteMobileTeamAsync(Guid teamId, CancellationToken cancellationToken = default) =>
        HostOnlyAsync();

    public async Task<MobileTeam> DeployMobileTeamAsync(Guid teamId, string? location, CancellationToken cancellationToken = default)
    {
        await SendCommandAsync(NewCommand(TerminalCommandKind.DeployMobileTeam) with { TargetId = teamId, Location = location }, cancellationToken);
        return ToMobileTeam((await RefreshAsync(cancellationToken)).MobileTeams.Single(team => team.Id == teamId));
    }

    public async Task<MobileTeam> UpdateMobileTeamLocationAsync(Guid teamId, string? location, CancellationToken cancellationToken = default)
    {
        await SendCommandAsync(NewCommand(TerminalCommandKind.UpdateMobileTeamLocation) with { TargetId = teamId, Location = location }, cancellationToken);
        return ToMobileTeam((await RefreshAsync(cancellationToken)).MobileTeams.Single(team => team.Id == teamId));
    }

    public async Task<MobileTeam> StandDownMobileTeamAsync(Guid teamId, CancellationToken cancellationToken = default)
    {
        await SendCommandAsync(NewCommand(TerminalCommandKind.StandDownMobileTeam) with { TargetId = teamId }, cancellationToken);
        return ToMobileTeam((await RefreshAsync(cancellationToken)).MobileTeams.Single(team => team.Id == teamId));
    }

    public async Task<Patient> AddPatientAsync(Guid stationId, string? presentingComplaint, CancellationToken cancellationToken = default)
    {
        await SendCommandAsync(NewCommand(TerminalCommandKind.AddPatientToStation) with { TargetId = stationId }, cancellationToken);
        var patient = (await RefreshAsync(cancellationToken)).Stations.Single(station => station.Id == stationId).Patient;
        return ToPatient(patient, stationId, null)
            ?? throw new InvalidOperationException("The host accepted the patient but the station remained available.");
    }

    public async Task<Patient> AddPatientToMobileTeamAsync(Guid teamId, string? presentingComplaint, CancellationToken cancellationToken = default)
    {
        await SendCommandAsync(NewCommand(TerminalCommandKind.AddPatientToMobileTeam) with { TargetId = teamId }, cancellationToken);
        var patient = (await RefreshAsync(cancellationToken)).MobileTeams.Single(team => team.Id == teamId).Patient;
        return ToPatient(patient, null, teamId)
            ?? throw new InvalidOperationException("The host accepted the patient but the mobile team remained available.");
    }

    public async Task<IReadOnlyList<Patient>> GetPatientsAsync(CancellationToken cancellationToken = default)
    {
        var snapshot = await CurrentOrRefreshAsync(cancellationToken);
        return snapshot.Stations
            .Select(station => ToPatient(station.Patient, station.Id, null))
            .Concat(snapshot.MobileTeams.Select(team => ToPatient(team.Patient, null, team.Id)))
            .OfType<Patient>()
            .ToList();
    }

    public Task<Patient> UpdatePatientDetailsAsync(Guid patientUid, DateTimeOffset addedAt, DateTimeOffset? dischargedAt, string? presentingComplaint, string? dischargeRoute, string? dischargeOutcome, CancellationToken cancellationToken = default) =>
        HostOnlyAsync<Patient>();

    public Task UpdatePresentingComplaintAsync(IReadOnlyCollection<Guid> patientUids, string presentingComplaint, CancellationToken cancellationToken = default) =>
        HostOnlyAsync();

    public Task DeletePatientAsync(Guid patientUid, CancellationToken cancellationToken = default) =>
        HostOnlyAsync();

    public async Task DischargePatientAsync(Guid stationId, string? dischargeRoute, string? dischargeOutcome, CancellationToken cancellationToken = default)
    {
        var snapshot = await CurrentOrRefreshAsync(cancellationToken);
        var patient = snapshot.Stations.SingleOrDefault(station => station.Id == stationId)?.Patient
            ?? throw new InvalidOperationException("The station no longer has a patient.");
        await DischargeAssignedPatientAsync(patient.Reference, dischargeRoute, dischargeOutcome, cancellationToken);
    }

    public async Task DischargeAssignedPatientAsync(Guid patientUid, string? dischargeRoute, string? dischargeOutcome, CancellationToken cancellationToken = default)
    {
        await SendCommandAsync(NewCommand(TerminalCommandKind.DischargePatient) with
        {
            TargetId = patientUid,
            DischargeRoute = dischargeRoute,
            DischargeOutcome = dischargeOutcome
        }, cancellationToken);
        await RefreshAsync(cancellationToken);
    }

    public async Task<PatientTransferResult> MovePatientAsync(Guid sourceStationId, Guid destinationStationId, bool swap, CancellationToken cancellationToken = default)
    {
        var snapshot = await CurrentOrRefreshAsync(cancellationToken);
        var patient = snapshot.Stations.SingleOrDefault(station => station.Id == sourceStationId)?.Patient
            ?? throw new InvalidOperationException("The source station no longer has a patient.");
        return await MovePatientAsync(
            patient.Reference,
            new PatientAssignment(PatientAssignmentKind.Station, destinationStationId),
            swap,
            cancellationToken);
    }

    public async Task<PatientTransferResult> MovePatientAsync(Guid patientUid, PatientAssignment destination, bool swap, CancellationToken cancellationToken = default)
    {
        var before = await CurrentOrRefreshAsync(cancellationToken);
        var sourceAssignment = FindAssignment(before, patientUid);
        await SendCommandAsync(NewCommand(TerminalCommandKind.MovePatient) with
        {
            TargetId = patientUid,
            SecondaryId = destination.Id,
            DestinationKind = destination.Kind == PatientAssignmentKind.MobileTeam
                ? TerminalAssignmentKind.MobileTeam
                : TerminalAssignmentKind.Station,
            Swap = swap
        }, cancellationToken);
        var after = await RefreshAsync(cancellationToken);
        var moved = FindPatient(after, patientUid)
            ?? throw new InvalidOperationException("The moved patient was not present after host refresh.");
        var swapped = sourceAssignment is null ? null : FindPatientAt(after, sourceAssignment);
        return new PatientTransferResult(moved, swapped?.Uid == moved.Uid ? null : swapped);
    }

    public async Task<int> GetPatientsSeenThisShiftAsync(CancellationToken cancellationToken = default) =>
        (await CurrentOrRefreshAsync(cancellationToken)).Dashboard.PatientsSeen;

    public async Task<DashboardSnapshot> GetDashboardAsync(CancellationToken cancellationToken = default)
    {
        var dashboard = (await CurrentOrRefreshAsync(cancellationToken)).Dashboard;
        return new DashboardSnapshot(
            dashboard.AvailableStations,
            dashboard.OccupiedStations,
            dashboard.PatientsSeen,
            dashboard.AverageDischargeTicks is { } ticks ? TimeSpan.FromTicks(ticks) : null,
            [],
            [],
            [],
            [],
            dashboard.Occupancy.Select(point => new OccupancyPoint(point.ObservedAt, (int)point.Value)).ToList(),
            dashboard.CumulativeArrivals.Select(point => new CumulativeArrivalPoint(point.ObservedAt, (int)point.Value)).ToList());
    }

    public async Task AcknowledgeRejectedCommandsAsync(CancellationToken cancellationToken = default)
    {
        await queue.AcknowledgeRejectedAsync(cancellationToken);
        QueueChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task MarkPendingCommandsUnresolvedAsync(
        string reason,
        CancellationToken cancellationToken = default)
    {
        await queue.MarkPendingUnresolvedAsync(reason, cancellationToken);
        QueueChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task AcknowledgeUnresolvedCommandsAsync(CancellationToken cancellationToken = default)
    {
        await queue.AcknowledgeUnresolvedAsync(cancellationToken);
        QueueChanged?.Invoke(this, EventArgs.Empty);
    }

    public Task<IReadOnlyList<QueuedTerminalCommand>> GetQueuedCommandsAsync(CancellationToken cancellationToken = default) =>
        queue.GetAsync(cancellationToken);

    public async Task<TerminalSnapshotResponse> RefreshAsync(CancellationToken cancellationToken = default)
    {
        await _refreshGate.WaitAsync(cancellationToken);
        try
        {
            await FlushQueueCoreAsync(cancellationToken);
            _snapshot = await apiClient.GetSnapshotAsync(cancellationToken);
            return _snapshot;
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    public void Dispose()
    {
        queue.Dispose();
        apiClient.Dispose();
        _refreshGate.Dispose();
    }

    private async Task<TerminalSnapshotResponse> CurrentOrRefreshAsync(CancellationToken cancellationToken) =>
        _snapshot ?? await RefreshAsync(cancellationToken);

    private async Task SendCommandAsync(TerminalCommandRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var response = await apiClient.SendCommandAsync(request, cancellationToken);
            if (response.Status == TerminalCommandStatus.Rejected)
            {
                throw new TerminalCommandRejectedException(response.ErrorCode ?? "rejected", response.Message ?? "The host rejected this command.", response.Sequence);
            }
        }
        catch (Exception exception) when (IsConnectionFailure(exception, cancellationToken))
        {
            await queue.EnqueueAsync(request, CancellationToken.None);
            QueueChanged?.Invoke(this, EventArgs.Empty);
            throw new TerminalCommandQueuedException("The host connection was lost. This command is encrypted locally and will be revalidated when the terminal reconnects.", exception);
        }
    }

    private async Task FlushQueueCoreAsync(CancellationToken cancellationToken)
    {
        var queued = await queue.GetAsync(cancellationToken);
        foreach (var item in queued.Where(item => item.State == QueuedTerminalCommandState.Pending))
        {
            try
            {
                var response = await apiClient.SendCommandAsync(item.Command, cancellationToken);
                if (response.Status == TerminalCommandStatus.Accepted)
                {
                    await queue.RemoveAsync(item.Command.RequestId, cancellationToken);
                }
                else
                {
                    await queue.RejectAsync(
                        item.Command.RequestId,
                        response.Sequence,
                        response.Message ?? "The host rejected this queued command.",
                        cancellationToken);
                }
                QueueChanged?.Invoke(this, EventArgs.Empty);
            }
            catch (Exception exception) when (IsConnectionFailure(exception, cancellationToken))
            {
                return;
            }
        }
    }

    private TerminalCommandRequest NewCommand(TerminalCommandKind kind) => new(
        Guid.NewGuid(),
        kind,
        _snapshot?.Sequence,
        CreatedAt: DateTimeOffset.UtcNow);

    private static bool IsConnectionFailure(Exception exception, CancellationToken cancellationToken) =>
        exception is HttpRequestException
        || exception is TaskCanceledException && !cancellationToken.IsCancellationRequested;

    private static Patient? ToPatient(TerminalPatient? patient, Guid? stationId, Guid? teamId) => patient is null
        ? null
        : new Patient(patient.Reference, patient.Number, patient.AddedAt, stationId, null, null, null, null, teamId);

    private static MobileTeam ToMobileTeam(TerminalMobileTeam team) =>
        new(team.Id, team.Callsign, team.Note, team.IsDeployed, team.DeploymentLocation);

    private static Patient? FindPatient(TerminalSnapshotResponse snapshot, Guid reference) =>
        snapshot.Stations
            .Select(station => ToPatient(station.Patient, station.Id, null))
            .Concat(snapshot.MobileTeams.Select(team => ToPatient(team.Patient, null, team.Id)))
            .FirstOrDefault(patient => patient?.Uid == reference);

    private static PatientAssignment? FindAssignment(TerminalSnapshotResponse snapshot, Guid reference)
    {
        var station = snapshot.Stations.FirstOrDefault(item => item.Patient?.Reference == reference);
        if (station is not null)
        {
            return new PatientAssignment(PatientAssignmentKind.Station, station.Id);
        }

        var team = snapshot.MobileTeams.FirstOrDefault(item => item.Patient?.Reference == reference);
        return team is null ? null : new PatientAssignment(PatientAssignmentKind.MobileTeam, team.Id);
    }

    private static Patient? FindPatientAt(TerminalSnapshotResponse snapshot, PatientAssignment assignment) =>
        assignment.Kind == PatientAssignmentKind.Station
            ? snapshot.Stations.Where(item => item.Id == assignment.Id).Select(item => ToPatient(item.Patient, item.Id, null)).SingleOrDefault()
            : snapshot.MobileTeams.Where(item => item.Id == assignment.Id).Select(item => ToPatient(item.Patient, null, item.Id)).SingleOrDefault();

    private static Task HostOnlyAsync() =>
        Task.FromException(new InvalidOperationException("This administration action is available only on the authoritative host."));

    private static Task<T> HostOnlyAsync<T>() =>
        Task.FromException<T>(new InvalidOperationException("This administration action is available only on the authoritative host."));
}

public sealed class TerminalCommandQueuedException(string message, Exception innerException) : InvalidOperationException(message, innerException);

public sealed class TerminalCommandRejectedException(string code, string message, long sequence) : InvalidOperationException(message)
{
    public string Code { get; } = code;
    public long Sequence { get; } = sequence;
}
