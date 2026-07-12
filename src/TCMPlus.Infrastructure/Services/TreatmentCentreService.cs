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
        if (await patientRepository.GetByStationAsync(stationId, cancellationToken) is not null) throw new InvalidOperationException("Remove the current patient before deleting this station.");
        await stationRepository.DeleteAsync(stationId, cancellationToken);
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

    public async Task DischargePatientAsync(Guid stationId, string? dischargeRoute, CancellationToken cancellationToken = default)
    {
        var station = await FindStationAsync(stationId, cancellationToken);
        var discharged = await patientRepository.DischargeFromStationAsync(stationId, DateTimeOffset.UtcNow, dischargeRoute, cancellationToken)
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
        var events = await patientRepository.GetAllEventsAsync(cancellationToken);
        var completed = patients.Where(patient => patient.DischargedAt is not null).ToList();
        var durations = completed.Select(patient => new DischargeDurationPoint(patient.DischargedAt!.Value, patient.DischargedAt!.Value - patient.AddedAt)).OrderBy(point => point.DischargedAt).ToList();
        var complaintBreakdown = patients.Where(patient => !string.IsNullOrWhiteSpace(patient.PresentingComplaint))
            .GroupBy(patient => patient.PresentingComplaint!.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(group => new ComplaintBreakdown(group.First().PresentingComplaint!.Trim(), group.Count())).OrderByDescending(item => item.Count).ToList();
        var throughput = completed.GroupBy(patient => new DateTimeOffset(patient.DischargedAt!.Value.Year, patient.DischargedAt!.Value.Month, patient.DischargedAt!.Value.Day, patient.DischargedAt!.Value.Hour, 0, 0, TimeSpan.Zero))
            .Select(group => new ThroughputPoint(group.Key, group.Count())).OrderBy(point => point.BucketStart).ToList();
        var occupied = patients.Count(patient => patient.CurrentStationId is not null);
        TimeSpan? average = durations.Count == 0 ? null : TimeSpan.FromTicks((long)durations.Average(point => point.Duration.Ticks));
        return new DashboardSnapshot(stations.Count - occupied, occupied, patients.Count, average, events.Take(12).ToList(), complaintBreakdown, throughput, durations);
    }

    private async Task<Station> FindStationAsync(Guid stationId, CancellationToken cancellationToken) =>
        (await stationRepository.GetAllAsync(cancellationToken)).FirstOrDefault(station => station.Id == stationId)
        ?? throw new InvalidOperationException("The requested station no longer exists.");

    private Task AddEventAsync(Patient patient, PatientEventType type, string? from, string? to, CancellationToken cancellationToken) =>
        patientRepository.AddEventAsync(new PatientEvent(Guid.NewGuid(), patient.Uid, patient.PatientNumber, type, DateTimeOffset.UtcNow, from, to), cancellationToken);
}
