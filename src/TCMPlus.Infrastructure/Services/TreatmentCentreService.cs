using TCMPlus.Domain.Models;
using TCMPlus.Domain.Persistence;
using TCMPlus.Domain.Services;

namespace TCMPlus.Infrastructure.Services;

public sealed class TreatmentCentreService(
    IStationRepository stationRepository,
    IPatientRepository patientRepository,
    IMobileTeamRepository? mobileTeamRepository = null) : ITreatmentCentreService
{
    private readonly IMobileTeamRepository _mobileTeamRepository = mobileTeamRepository ?? new EmptyMobileTeamRepository();

    public async Task<IReadOnlyList<StationSnapshot>> GetSnapshotAsync(CancellationToken cancellationToken = default)
    {
        var stations = await stationRepository.GetAllAsync(cancellationToken);
        var patients = await patientRepository.GetAllActiveAsync(cancellationToken);
        var patientByStation = patients.Where(patient => patient.CurrentStationId is not null).ToDictionary(patient => patient.CurrentStationId!.Value);
        return stations.Select(station => new StationSnapshot(station, patientByStation.GetValueOrDefault(station.Id))).ToList();
    }

    public async Task<IReadOnlyList<MobileTeamSnapshot>> GetMobileTeamsAsync(CancellationToken cancellationToken = default)
    {
        var teams = await _mobileTeamRepository.GetAllAsync(cancellationToken);
        var patients = await patientRepository.GetAllActiveAsync(cancellationToken);
        var patientByTeam = patients.Where(patient => patient.CurrentMobileTeamId is not null).ToDictionary(patient => patient.CurrentMobileTeamId!.Value);
        return teams.Select(team => new MobileTeamSnapshot(team, patientByTeam.GetValueOrDefault(team.Id))).ToList();
    }

    public async Task<Station> AddStationAsync(string name, string type, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(type)) throw new InvalidOperationException("Stations require both a name and type.");
        var count = (await stationRepository.GetAllAsync(cancellationToken)).Count;
        var offset = (count % 5) * 2d;
        var station = new Station(Guid.NewGuid(), name.Trim(), type.Trim(), 1 + offset, 1 + offset, 8, 7);
        await stationRepository.AddAsync(station, cancellationToken);
        return station;
    }

    public Task SaveStationAsync(Station station, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(station.Name) || string.IsNullOrWhiteSpace(station.Type)) throw new InvalidOperationException("Stations require both a name and type.");
        if (station.GridWidth < 7 || station.GridHeight < 7) throw new InvalidOperationException("Stations must be at least 7 by 7 grid units.");
        return stationRepository.UpdateAsync(station, cancellationToken);
    }

    public async Task ReorderStationsAsync(IReadOnlyList<Guid> stationIds, CancellationToken cancellationToken = default)
    {
        var existingStationIds = (await stationRepository.GetAllAsync(cancellationToken)).Select(station => station.Id).ToHashSet();
        if (stationIds.Count != existingStationIds.Count
            || stationIds.Distinct().Count() != stationIds.Count
            || stationIds.Any(stationId => !existingStationIds.Contains(stationId)))
        {
            throw new InvalidOperationException("The station order is out of date. Reload the shift and try again.");
        }

        await stationRepository.UpdateOrderAsync(stationIds, cancellationToken);
    }

    public async Task DeleteStationAsync(Guid stationId, CancellationToken cancellationToken = default)
    {
        if (await patientRepository.GetByStationAsync(stationId, cancellationToken) is not null) throw new InvalidOperationException("Discharge or delete the current patient before deleting this station.");
        if ((await stationRepository.GetAllAsync(cancellationToken)).All(station => station.Id != stationId)) throw new InvalidOperationException("The requested station no longer exists.");
        await stationRepository.SoftDeleteAsync(stationId, DateTimeOffset.UtcNow, cancellationToken);
    }

    public async Task<MobileTeam> AddMobileTeamAsync(string callsign, string? note, CancellationToken cancellationToken = default)
    {
        var normalizedCallsign = NormalizeRequiredCallsign(callsign);
        await EnsureUniqueCallsignAsync(normalizedCallsign, null, cancellationToken);
        var team = new MobileTeam(Guid.NewGuid(), normalizedCallsign, NormalizeOptionalText(note), false, null);
        await _mobileTeamRepository.AddAsync(team, cancellationToken);
        return team;
    }

    public async Task<MobileTeam> UpdateMobileTeamAsync(Guid teamId, string callsign, string? note, CancellationToken cancellationToken = default)
    {
        var existing = await FindMobileTeamAsync(teamId, cancellationToken);
        var normalizedCallsign = NormalizeRequiredCallsign(callsign);
        await EnsureUniqueCallsignAsync(normalizedCallsign, teamId, cancellationToken);
        var updated = existing with { Callsign = normalizedCallsign, Note = NormalizeOptionalText(note) };
        await _mobileTeamRepository.UpdateAsync(updated, cancellationToken);
        return updated;
    }

    public async Task DeleteMobileTeamAsync(Guid teamId, CancellationToken cancellationToken = default)
    {
        var team = await FindMobileTeamAsync(teamId, cancellationToken);
        if (team.IsDeployed)
        {
            throw new InvalidOperationException("Stand down this mobile team before deleting it.");
        }

        if (await patientRepository.GetByMobileTeamAsync(teamId, cancellationToken) is not null)
        {
            throw new InvalidOperationException("Transfer, discharge, or delete the current patient before deleting this mobile team.");
        }

        await _mobileTeamRepository.SoftDeleteAsync(teamId, DateTimeOffset.UtcNow, cancellationToken);
    }

    public async Task<MobileTeam> DeployMobileTeamAsync(Guid teamId, string? location, CancellationToken cancellationToken = default)
    {
        var team = await FindMobileTeamAsync(teamId, cancellationToken);
        if (team.IsDeployed)
        {
            throw new InvalidOperationException("This mobile team is already deployed.");
        }

        var deployed = team with { IsDeployed = true, DeploymentLocation = NormalizeOptionalText(location) };
        await _mobileTeamRepository.UpdateAsync(deployed, cancellationToken);
        return deployed;
    }

    public async Task<MobileTeam> UpdateMobileTeamLocationAsync(Guid teamId, string? location, CancellationToken cancellationToken = default)
    {
        var team = await FindMobileTeamAsync(teamId, cancellationToken);
        if (!team.IsDeployed)
        {
            throw new InvalidOperationException("Deploy this mobile team before setting its location.");
        }

        var updated = team with { DeploymentLocation = NormalizeOptionalText(location) };
        await _mobileTeamRepository.UpdateAsync(updated, cancellationToken);
        return updated;
    }

    public async Task<MobileTeam> StandDownMobileTeamAsync(Guid teamId, CancellationToken cancellationToken = default)
    {
        var team = await FindMobileTeamAsync(teamId, cancellationToken);
        if (!team.IsDeployed)
        {
            throw new InvalidOperationException("This mobile team is already available.");
        }

        if (await patientRepository.GetByMobileTeamAsync(teamId, cancellationToken) is not null)
        {
            throw new InvalidOperationException("Transfer or discharge the current patient before standing down this mobile team.");
        }

        var available = team with { IsDeployed = false, DeploymentLocation = null };
        await _mobileTeamRepository.UpdateAsync(available, cancellationToken);
        return available;
    }

    public async Task<Patient> AddPatientAsync(Guid stationId, string? presentingComplaint, CancellationToken cancellationToken = default)
    {
        if (await patientRepository.GetByStationAsync(stationId, cancellationToken) is not null) throw new InvalidOperationException("This station is already occupied.");
        var station = await FindStationAsync(stationId, cancellationToken);
        var patient = new Patient(Guid.NewGuid(), await patientRepository.GetNextPatientNumberAsync(cancellationToken), DateTimeOffset.UtcNow, stationId, string.IsNullOrWhiteSpace(presentingComplaint) ? null : presentingComplaint.Trim(), null, null);
        await patientRepository.AddAsync(patient, cancellationToken);
        await AddEventAsync(patient, PatientEventType.Added, null, station.Name, null, PatientAssignmentKind.Station, cancellationToken);
        return patient;
    }

    public async Task<Patient> AddPatientToMobileTeamAsync(Guid teamId, string? presentingComplaint, CancellationToken cancellationToken = default)
    {
        if (await patientRepository.GetByMobileTeamAsync(teamId, cancellationToken) is not null)
        {
            throw new InvalidOperationException("This mobile team already has a patient.");
        }

        var team = await FindMobileTeamAsync(teamId, cancellationToken);
        var patient = new Patient(
            Guid.NewGuid(),
            await patientRepository.GetNextPatientNumberAsync(cancellationToken),
            DateTimeOffset.UtcNow,
            null,
            NormalizeOptionalText(presentingComplaint),
            null,
            null,
            null,
            teamId);
        await patientRepository.AddAsync(patient, cancellationToken);
        await AddEventAsync(patient, PatientEventType.Added, null, team.Callsign, null, PatientAssignmentKind.MobileTeam, cancellationToken);
        return patient;
    }

    public Task<IReadOnlyList<Patient>> GetPatientsAsync(CancellationToken cancellationToken = default) =>
        patientRepository.GetAllAsync(cancellationToken);

    public async Task<Patient> UpdatePatientDetailsAsync(Guid patientUid, DateTimeOffset addedAt, DateTimeOffset? dischargedAt, string? presentingComplaint, string? dischargeRoute, string? dischargeOutcome, CancellationToken cancellationToken = default)
    {
        var patient = (await patientRepository.GetAllAsync(cancellationToken)).FirstOrDefault(item => item.Uid == patientUid)
            ?? throw new InvalidOperationException("The requested patient no longer exists.");
        var normalizedRoute = NormalizeOptionalText(dischargeRoute);
        var normalizedOutcome = NormalizeOptionalText(dischargeOutcome);
        if (dischargedAt is not null && dischargedAt <= addedAt)
        {
            throw new InvalidOperationException("Discharge time must be after the patient's new time.");
        }

        if (patient.DischargedAt is null && dischargedAt is not null)
        {
            throw new InvalidOperationException("Discharge active patients from their station.");
        }

        if (patient.DischargedAt is not null && dischargedAt is null)
        {
            throw new InvalidOperationException("Discharged patients require a discharge time.");
        }

        if (dischargedAt is null && (normalizedRoute is not null || normalizedOutcome is not null))
        {
            throw new InvalidOperationException("Only discharged patients can have a discharge route or outcome.");
        }

        var updated = patient with
        {
            AddedAt = addedAt,
            DischargedAt = dischargedAt,
            PresentingComplaint = NormalizeOptionalText(presentingComplaint),
            DischargeRoute = dischargedAt is null ? null : normalizedRoute,
            DischargeOutcome = dischargedAt is null ? null : normalizedOutcome
        };
        await patientRepository.UpdateDetailsAsync(updated, cancellationToken);
        return updated;
    }

    public async Task UpdatePresentingComplaintAsync(IReadOnlyCollection<Guid> patientUids, string presentingComplaint, CancellationToken cancellationToken = default)
    {
        if (patientUids.Count == 0)
        {
            throw new InvalidOperationException("Select at least one patient.");
        }

        var normalizedComplaint = NormalizeOptionalText(presentingComplaint)
            ?? throw new InvalidOperationException("Enter a presenting complaint.");
        var existingUids = (await patientRepository.GetAllAsync(cancellationToken)).Select(patient => patient.Uid).ToHashSet();
        if (patientUids.Any(uid => !existingUids.Contains(uid)))
        {
            throw new InvalidOperationException("One or more selected patients no longer exist.");
        }

        await patientRepository.UpdatePresentingComplaintAsync(patientUids, normalizedComplaint, cancellationToken);
    }

    public async Task DeletePatientAsync(Guid patientUid, CancellationToken cancellationToken = default)
    {
        if ((await patientRepository.GetAllAsync(cancellationToken)).All(patient => patient.Uid != patientUid))
        {
            throw new InvalidOperationException("The requested patient no longer exists.");
        }

        await patientRepository.DeleteAsync(patientUid, cancellationToken);
    }

    public async Task DischargePatientAsync(Guid stationId, string? dischargeRoute, string? dischargeOutcome, CancellationToken cancellationToken = default)
    {
        var station = await FindStationAsync(stationId, cancellationToken);
        var dischargedAt = DateTimeOffset.UtcNow;
        var currentPatient = await patientRepository.GetByStationAsync(stationId, cancellationToken)
            ?? throw new InvalidOperationException("This station is already available.");
        if (dischargedAt <= currentPatient.AddedAt)
        {
            throw new InvalidOperationException("Discharge time must be after the patient's new time.");
        }

        var discharged = await patientRepository.DischargeFromStationAsync(stationId, dischargedAt, NormalizeOptionalText(dischargeRoute), NormalizeOptionalText(dischargeOutcome), cancellationToken)
            ?? throw new InvalidOperationException("This station is already available.");
        await AddEventAsync(discharged, PatientEventType.Discharged, station.Name, null, PatientAssignmentKind.Station, null, cancellationToken);
    }

    public async Task DischargeAssignedPatientAsync(Guid patientUid, string? dischargeRoute, string? dischargeOutcome, CancellationToken cancellationToken = default)
    {
        var patient = await patientRepository.GetByUidAsync(patientUid, cancellationToken)
            ?? throw new InvalidOperationException("The requested patient no longer exists.");
        var assignment = CurrentAssignment(patient)
            ?? throw new InvalidOperationException("This patient is not currently assigned.");
        var locationName = await AssignmentNameAsync(assignment, cancellationToken);
        var dischargedAt = DateTimeOffset.UtcNow;
        if (dischargedAt <= patient.AddedAt)
        {
            throw new InvalidOperationException("Discharge time must be after the patient's new time.");
        }

        var discharged = await patientRepository.DischargeAsync(patientUid, dischargedAt, NormalizeOptionalText(dischargeRoute), NormalizeOptionalText(dischargeOutcome), cancellationToken)
            ?? throw new InvalidOperationException("This patient is already discharged.");
        await AddEventAsync(discharged, PatientEventType.Discharged, locationName, null, assignment.Kind, null, cancellationToken);
    }

    public async Task<PatientTransferResult> MovePatientAsync(Guid sourceStationId, Guid destinationStationId, bool swap, CancellationToken cancellationToken = default)
    {
        var patient = await patientRepository.GetByStationAsync(sourceStationId, cancellationToken)
            ?? throw new InvalidOperationException("The source station no longer has a patient.");
        return await MovePatientAsync(patient.Uid, new PatientAssignment(PatientAssignmentKind.Station, destinationStationId), swap, cancellationToken);
    }

    public async Task<PatientTransferResult> MovePatientAsync(Guid patientUid, PatientAssignment destination, bool swap, CancellationToken cancellationToken = default)
    {
        var sourcePatient = await patientRepository.GetByUidAsync(patientUid, cancellationToken)
            ?? throw new InvalidOperationException("The requested patient no longer exists.");
        var source = CurrentAssignment(sourcePatient)
            ?? throw new InvalidOperationException("This patient is not currently assigned.");
        if (source == destination)
        {
            throw new InvalidOperationException("Choose a different destination.");
        }

        if (source.Kind == PatientAssignmentKind.MobileTeam && destination.Kind == PatientAssignmentKind.MobileTeam)
        {
            throw new InvalidOperationException("Move mobile-team patients through a treatment-centre station.");
        }

        var sourceName = await AssignmentNameAsync(source, cancellationToken);
        var destinationName = await AssignmentNameAsync(destination, cancellationToken);
        if (source.Kind == PatientAssignmentKind.Station && destination.Kind == PatientAssignmentKind.MobileTeam)
        {
            var team = await FindMobileTeamAsync(destination.Id, cancellationToken);
            if (!team.IsDeployed)
            {
                throw new InvalidOperationException("Deploy the destination mobile team before transferring a patient to it.");
            }
        }

        var allowSwap = swap && source.Kind == PatientAssignmentKind.Station && destination.Kind == PatientAssignmentKind.Station;
        var result = await patientRepository.MoveAsync(patientUid, destination, allowSwap, cancellationToken);
        await AddEventAsync(result.SourcePatient, PatientEventType.Transferred, sourceName, destinationName, source.Kind, destination.Kind, cancellationToken);
        if (result.SwappedPatient is not null)
        {
            await AddEventAsync(result.SwappedPatient, PatientEventType.Transferred, destinationName, sourceName, destination.Kind, source.Kind, cancellationToken);
        }

        return result;
    }

    public async Task<int> GetPatientsSeenThisShiftAsync(CancellationToken cancellationToken = default) => (await patientRepository.GetAllAsync(cancellationToken)).Count;

    public async Task<DashboardSnapshot> GetDashboardAsync(CancellationToken cancellationToken = default)
    {
        var stations = await stationRepository.GetAllAsync(cancellationToken);
        var patients = await patientRepository.GetAllAsync(cancellationToken);
        var events = await patientRepository.GetAllEventsAsync(cancellationToken);
        var completed = patients.Where(patient => patient.DischargedAt is not null).ToList();
        var durations = completed.Select(patient => new DischargeDurationPoint(patient.DischargedAt!.Value, patient.DischargedAt!.Value - patient.AddedAt)).OrderBy(point => point.DischargedAt).ToList();
        var complaintBreakdown = patients.Where(patient => !string.IsNullOrWhiteSpace(patient.PresentingComplaint))
            .GroupBy(patient => patient.PresentingComplaint!.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(group => new ComplaintBreakdown(group.First().PresentingComplaint!.Trim(), group.Count())).OrderByDescending(item => item.Count).ToList();
        var dischargeRouteBreakdown = completed.Where(patient => !string.IsNullOrWhiteSpace(patient.DischargeRoute))
            .GroupBy(patient => patient.DischargeRoute!.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(group => new DischargeRouteBreakdown(group.Key, group.Count())).OrderByDescending(item => item.Count).ToList();
        var throughput = completed.GroupBy(patient => new DateTimeOffset(patient.DischargedAt!.Value.Year, patient.DischargedAt!.Value.Month, patient.DischargedAt!.Value.Day, patient.DischargedAt!.Value.Hour, 0, 0, TimeSpan.Zero))
            .Select(group => new ThroughputPoint(group.Key, group.Count())).OrderBy(point => point.BucketStart).ToList();
        var occupied = patients.Count(patient => patient.CurrentStationId is not null);
        TimeSpan? average = durations.Count == 0 ? null : TimeSpan.FromTicks((long)durations.Average(point => point.Duration.Ticks));
        var (occupancy, cumulativeArrivals) = BuildFifteenMinuteSeries(patients, events, DateTimeOffset.UtcNow);
        return new DashboardSnapshot(stations.Count - occupied, occupied, patients.Count, average, complaintBreakdown, dischargeRouteBreakdown, throughput, durations, occupancy, cumulativeArrivals);
    }

    private async Task<Station> FindStationAsync(Guid stationId, CancellationToken cancellationToken) =>
        (await stationRepository.GetAllAsync(cancellationToken)).FirstOrDefault(station => station.Id == stationId)
        ?? throw new InvalidOperationException("The requested station no longer exists.");

    private async Task<MobileTeam> FindMobileTeamAsync(Guid teamId, CancellationToken cancellationToken) =>
        await _mobileTeamRepository.GetByIdAsync(teamId, cancellationToken)
        ?? throw new InvalidOperationException("The requested mobile team no longer exists.");

    private Task AddEventAsync(
        Patient patient,
        PatientEventType type,
        string? from,
        string? to,
        PatientAssignmentKind? fromKind,
        PatientAssignmentKind? toKind,
        CancellationToken cancellationToken) =>
        patientRepository.AddEventAsync(new PatientEvent(Guid.NewGuid(), patient.Uid, patient.PatientNumber, type, DateTimeOffset.UtcNow, from, to, fromKind, toKind), cancellationToken);

    private async Task<string> AssignmentNameAsync(PatientAssignment assignment, CancellationToken cancellationToken) =>
        assignment.Kind == PatientAssignmentKind.Station
            ? (await FindStationAsync(assignment.Id, cancellationToken)).Name
            : (await FindMobileTeamAsync(assignment.Id, cancellationToken)).Callsign;

    private async Task EnsureUniqueCallsignAsync(string callsign, Guid? excludingTeamId, CancellationToken cancellationToken)
    {
        if ((await _mobileTeamRepository.GetAllAsync(cancellationToken)).Any(team =>
                team.Id != excludingTeamId
                && string.Equals(team.Callsign, callsign, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException("Mobile-team callsigns must be unique.");
        }
    }

    private static string? NormalizeOptionalText(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static string NormalizeRequiredCallsign(string value) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new InvalidOperationException("Enter a mobile-team callsign.")
            : value.Trim();

    private static (IReadOnlyList<OccupancyPoint> Occupancy, IReadOnlyList<CumulativeArrivalPoint> CumulativeArrivals) BuildFifteenMinuteSeries(
        IReadOnlyList<Patient> patients,
        IReadOnlyList<PatientEvent> events,
        DateTimeOffset now)
    {
        var observedAt = now.ToUniversalTime();
        var start = FloorToQuarterHour(patients.Count == 0 ? observedAt : patients.Min(patient => patient.AddedAt));
        var eventsByPatient = events.GroupBy(patientEvent => patientEvent.PatientUid)
            .ToDictionary(group => group.Key, group => group.OrderBy(patientEvent => patientEvent.OccurredAt).ToList());
        var occupancy = new List<OccupancyPoint>();
        var cumulativeArrivals = new List<CumulativeArrivalPoint>();
        for (var intervalStart = start; intervalStart <= observedAt; intervalStart = intervalStart.AddMinutes(15))
        {
            var intervalEnd = intervalStart.AddMinutes(15) > observedAt ? observedAt : intervalStart.AddMinutes(15);
            var occupiedStations = patients.Count(patient =>
            {
                if (patient.AddedAt > intervalEnd)
                {
                    return false;
                }

                if (!eventsByPatient.TryGetValue(patient.Uid, out var patientEvents))
                {
                    return patient.CurrentStationId is not null && (patient.DischargedAt is null || patient.DischargedAt > intervalEnd);
                }

                var latest = patientEvents.LastOrDefault(patientEvent => patientEvent.OccurredAt <= intervalEnd);
                return latest is not null
                    && latest.Type != PatientEventType.Discharged
                    && latest.ToLocationKind == PatientAssignmentKind.Station;
            });
            occupancy.Add(new OccupancyPoint(intervalEnd, occupiedStations));
            cumulativeArrivals.Add(new CumulativeArrivalPoint(intervalEnd, patients.Count(patient => patient.AddedAt <= intervalEnd)));
        }

        return (occupancy, cumulativeArrivals);
    }

    private static DateTimeOffset FloorToQuarterHour(DateTimeOffset value)
    {
        var utc = value.ToUniversalTime();
        return new DateTimeOffset(utc.Year, utc.Month, utc.Day, utc.Hour, utc.Minute / 15 * 15, 0, TimeSpan.Zero);
    }

    private static PatientAssignment? CurrentAssignment(Patient patient) =>
        patient.CurrentStationId is Guid stationId
            ? new PatientAssignment(PatientAssignmentKind.Station, stationId)
            : patient.CurrentMobileTeamId is Guid teamId
                ? new PatientAssignment(PatientAssignmentKind.MobileTeam, teamId)
                : null;

    private sealed class EmptyMobileTeamRepository : IMobileTeamRepository
    {
        public Task<IReadOnlyList<MobileTeam>> GetAllAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<MobileTeam>>([]);
        public Task<MobileTeam?> GetByIdAsync(Guid teamId, CancellationToken cancellationToken = default) => Task.FromResult<MobileTeam?>(null);
        public Task AddAsync(MobileTeam team, CancellationToken cancellationToken = default) => throw new InvalidOperationException("Mobile-team persistence is not configured.");
        public Task UpdateAsync(MobileTeam team, CancellationToken cancellationToken = default) => throw new InvalidOperationException("Mobile-team persistence is not configured.");
        public Task SoftDeleteAsync(Guid teamId, DateTimeOffset deletedAt, CancellationToken cancellationToken = default) => throw new InvalidOperationException("Mobile-team persistence is not configured.");
    }
}
