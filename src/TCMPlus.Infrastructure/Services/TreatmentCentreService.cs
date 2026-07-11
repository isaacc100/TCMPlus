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
        var patientByStation = patients
            .Where(patient => patient.CurrentStationId is not null)
            .GroupBy(patient => patient.CurrentStationId!.Value)
            .ToDictionary(group => group.Key, group => group.First());

        return stations.Select(station => new StationSnapshot(
            station,
            patientByStation.GetValueOrDefault(station.Id))).ToList();
    }

    public async Task<Station> AddStationAsync(string name, string type, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(type))
        {
            throw new InvalidOperationException("Stations require both a name and type.");
        }

        var count = (await stationRepository.GetAllAsync(cancellationToken)).Count;
        var offset = (count % 5) * 2d;
        var station = new Station(
            Guid.NewGuid(),
            name.Trim(),
            type.Trim(),
            1 + offset,
            1 + offset,
            8,
            7);

        await stationRepository.AddAsync(station, cancellationToken);
        return station;
    }

    public Task SaveStationAsync(Station station, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(station.Name) || string.IsNullOrWhiteSpace(station.Type))
        {
            throw new InvalidOperationException("Stations require both a name and type.");
        }

        return stationRepository.UpdateAsync(station, cancellationToken);
    }

    public async Task DeleteStationAsync(Guid stationId, CancellationToken cancellationToken = default)
    {
        if (await patientRepository.GetByStationAsync(stationId, cancellationToken) is not null)
        {
            throw new InvalidOperationException("Remove the current patient before deleting this station.");
        }

        await stationRepository.DeleteAsync(stationId, cancellationToken);
    }

    public async Task<Patient> AddPatientAsync(Guid stationId, CancellationToken cancellationToken = default)
    {
        if (await patientRepository.GetByStationAsync(stationId, cancellationToken) is not null)
        {
            throw new InvalidOperationException("This station is already occupied.");
        }

        var patient = new Patient(Guid.NewGuid(), DateTimeOffset.UtcNow, stationId);
        await patientRepository.AddAsync(patient, cancellationToken);
        return patient;
    }

    public Task DischargePatientAsync(Guid stationId, CancellationToken cancellationToken = default) =>
        patientRepository.DischargeFromStationAsync(stationId, cancellationToken);

    public Task<int> GetPatientsSeenThisShiftAsync(CancellationToken cancellationToken = default) =>
        patientRepository.GetDischargedCountAsync(cancellationToken);
}
