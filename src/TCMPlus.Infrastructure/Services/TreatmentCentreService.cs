using TCMPlus.Domain.Models;
using TCMPlus.Domain.Persistence;
using TCMPlus.Domain.Services;

namespace TCMPlus.Infrastructure.Services;

public sealed class TreatmentCentreService(
    IStationRepository stationRepository,
    IPatientRepository patientRepository) : ITreatmentCentreService
{
    public async Task<IReadOnlyList<StationSnapshot>> GetSnapshotAsync(CancellationToken cancellationToken = default)
    {
        var stations = await stationRepository.GetAllAsync(cancellationToken);
        var patients = await patientRepository.GetAllActiveAsync(cancellationToken);
        var patientByStation = patients.Where(patient => patient.CurrentStationId is not null).ToDictionary(patient => patient.CurrentStationId!.Value);
        return stations.Select(station => new StationSnapshot(station, patientByStation.GetValueOrDefault(station.Id))).ToList();
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

    public async Task DeleteStationAsync(Guid stationId, CancellationToken cancellationToken = default)
    {
        if (await patientRepository.GetByStationAsync(stationId, cancellationToken) is not null) throw new InvalidOperationException("Discharge or delete the current patient before deleting this station.");
        if ((await stationRepository.GetAllAsync(cancellationToken)).All(station => station.Id != stationId)) throw new InvalidOperationException("The requested station no longer exists.");
        await stationRepository.SoftDeleteAsync(stationId, DateTimeOffset.UtcNow, cancellationToken);
    }

    public async Task<Patient> AddPatientAsync(Guid stationId, string? presentingComplaint, CancellationToken cancellationToken = default)
    {
        if (await patientRepository.GetByStationAsync(stationId, cancellationToken) is not null) throw new InvalidOperationException("This station is already occupied.");
        var station = await FindStationAsync(stationId, cancellationToken);
        var patient = new Patient(Guid.NewGuid(), await patientRepository.GetNextPatientNumberAsync(cancellationToken), DateTimeOffset.UtcNow, stationId, string.IsNullOrWhiteSpace(presentingComplaint) ? null : presentingComplaint.Trim(), null, null);
        await patientRepository.AddAsync(patient, cancellationToken);
        await AddEventAsync(patient, PatientEventType.Added, null, station.Name, cancellationToken);
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
        await AddEventAsync(discharged, PatientEventType.Discharged, station.Name, null, cancellationToken);
    }

    public async Task<PatientTransferResult> MovePatientAsync(Guid sourceStationId, Guid destinationStationId, bool swap, CancellationToken cancellationToken = default)
    {
        if (sourceStationId == destinationStationId) throw new InvalidOperationException("Choose a different destination station.");
        var sourceStation = await FindStationAsync(sourceStationId, cancellationToken);
        var destinationStation = await FindStationAsync(destinationStationId, cancellationToken);
        var result = await patientRepository.MoveAsync(sourceStationId, destinationStationId, swap, cancellationToken);
        await AddEventAsync(result.SourcePatient, PatientEventType.Transferred, sourceStation.Name, destinationStation.Name, cancellationToken);
        if (result.SwappedPatient is not null)
        {
            await AddEventAsync(result.SwappedPatient, PatientEventType.Transferred, destinationStation.Name, sourceStation.Name, cancellationToken);
        }
        return result;
    }

    public async Task<int> GetPatientsSeenThisShiftAsync(CancellationToken cancellationToken = default) => (await patientRepository.GetAllAsync(cancellationToken)).Count;

    public async Task<DashboardSnapshot> GetDashboardAsync(CancellationToken cancellationToken = default)
    {
        var stations = await stationRepository.GetAllAsync(cancellationToken);
        var patients = await patientRepository.GetAllAsync(cancellationToken);
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
        var (occupancy, cumulativeArrivals) = BuildFifteenMinuteSeries(patients, DateTimeOffset.UtcNow);
        return new DashboardSnapshot(stations.Count - occupied, occupied, patients.Count, average, complaintBreakdown, dischargeRouteBreakdown, throughput, durations, occupancy, cumulativeArrivals);
    }

    private async Task<Station> FindStationAsync(Guid stationId, CancellationToken cancellationToken) =>
        (await stationRepository.GetAllAsync(cancellationToken)).FirstOrDefault(station => station.Id == stationId)
        ?? throw new InvalidOperationException("The requested station no longer exists.");

    private Task AddEventAsync(Patient patient, PatientEventType type, string? from, string? to, CancellationToken cancellationToken) =>
        patientRepository.AddEventAsync(new PatientEvent(Guid.NewGuid(), patient.Uid, patient.PatientNumber, type, DateTimeOffset.UtcNow, from, to), cancellationToken);

    private static string? NormalizeOptionalText(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static (IReadOnlyList<OccupancyPoint> Occupancy, IReadOnlyList<CumulativeArrivalPoint> CumulativeArrivals) BuildFifteenMinuteSeries(IReadOnlyList<Patient> patients, DateTimeOffset now)
    {
        var observedAt = now.ToUniversalTime();
        var start = FloorToQuarterHour(patients.Count == 0 ? observedAt : patients.Min(patient => patient.AddedAt));
        var occupancy = new List<OccupancyPoint>();
        var cumulativeArrivals = new List<CumulativeArrivalPoint>();
        for (var intervalStart = start; intervalStart <= observedAt; intervalStart = intervalStart.AddMinutes(15))
        {
            var intervalEnd = intervalStart.AddMinutes(15) > observedAt ? observedAt : intervalStart.AddMinutes(15);
            occupancy.Add(new OccupancyPoint(intervalEnd, patients.Count(patient => patient.AddedAt <= intervalEnd && (patient.DischargedAt is null || patient.DischargedAt > intervalEnd))));
            cumulativeArrivals.Add(new CumulativeArrivalPoint(intervalEnd, patients.Count(patient => patient.AddedAt <= intervalEnd)));
        }

        return (occupancy, cumulativeArrivals);
    }

    private static DateTimeOffset FloorToQuarterHour(DateTimeOffset value)
    {
        var utc = value.ToUniversalTime();
        return new DateTimeOffset(utc.Year, utc.Month, utc.Day, utc.Hour, utc.Minute / 15 * 15, 0, TimeSpan.Zero);
    }
}
