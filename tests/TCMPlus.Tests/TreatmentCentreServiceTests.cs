using TCMPlus.Domain.Models;
using TCMPlus.Domain.Persistence;
using TCMPlus.Infrastructure.Services;

namespace TCMPlus.Tests;

public sealed class TreatmentCentreServiceTests
{
    [Fact]
    public async Task Allows_only_one_active_patient_per_station()
    {
        var station = new Station(Guid.NewGuid(), "Station 1", "Bed", 1, 1, 8, 7);
        var stations = new InMemoryStationRepository(station);
        var patients = new InMemoryPatientRepository();
        var service = new TreatmentCentreService(stations, patients);

        var patient = await service.AddPatientAsync(station.Id);

        Assert.Equal(station.Id, patient.CurrentStationId);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.AddPatientAsync(station.Id));

        await service.DischargePatientAsync(station.Id);
        Assert.Null(await patients.GetByStationAsync(station.Id));
        Assert.Equal(1, await service.GetPatientsSeenThisShiftAsync());
    }

    private sealed class InMemoryStationRepository(params Station[] stations) : IStationRepository
    {
        private readonly List<Station> _stations = [.. stations];

        public Task<IReadOnlyList<Station>> GetAllAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<Station>>(_stations);
        public Task AddAsync(Station station, CancellationToken cancellationToken = default) { _stations.Add(station); return Task.CompletedTask; }
        public Task UpdateAsync(Station station, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DeleteAsync(Guid stationId, CancellationToken cancellationToken = default) { _stations.RemoveAll(item => item.Id == stationId); return Task.CompletedTask; }
    }

    private sealed class InMemoryPatientRepository : IPatientRepository
    {
        private readonly List<Patient> _patients = [];

        public Task<IReadOnlyList<Patient>> GetAllActiveAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<Patient>>(_patients.Where(patient => patient.CurrentStationId is not null).ToList());
        public Task<int> GetDischargedCountAsync(CancellationToken cancellationToken = default) => Task.FromResult(_patients.Count(patient => patient.CurrentStationId is null));
        public Task<Patient?> GetByStationAsync(Guid stationId, CancellationToken cancellationToken = default) => Task.FromResult(_patients.FirstOrDefault(patient => patient.CurrentStationId == stationId));
        public Task AddAsync(Patient patient, CancellationToken cancellationToken = default) { _patients.Add(patient); return Task.CompletedTask; }
        public Task DischargeFromStationAsync(Guid stationId, CancellationToken cancellationToken = default)
        {
            for (var index = 0; index < _patients.Count; index++)
            {
                if (_patients[index].CurrentStationId == stationId)
                {
                    _patients[index] = _patients[index] with { CurrentStationId = null };
                }
            }

            return Task.CompletedTask;
        }
    }
}
