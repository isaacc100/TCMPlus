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

        var patient = await service.AddPatientAsync(station.Id, "Minor injury");

        Assert.Equal(station.Id, patient.CurrentStationId);
        Assert.Equal(1, patient.PatientNumber);
        Assert.Equal("Minor injury", patient.PresentingComplaint);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.AddPatientAsync(station.Id, null));

        await service.DischargePatientAsync(station.Id, "Conveyed");
        Assert.Null(await patients.GetByStationAsync(station.Id));
        Assert.Equal(1, await service.GetPatientsSeenThisShiftAsync());
    }

    [Fact]
    public async Task Transfers_and_swaps_patients_with_lifecycle_events()
    {
        var first = new Station(Guid.NewGuid(), "Bay 1", "Bed", 1, 1, 8, 7);
        var second = new Station(Guid.NewGuid(), "Bay 2", "Bed", 10, 1, 8, 7);
        var patients = new InMemoryPatientRepository();
        var service = new TreatmentCentreService(new InMemoryStationRepository(first, second), patients);
        var patientOne = await service.AddPatientAsync(first.Id, null);
        await service.MovePatientAsync(first.Id, second.Id, false);
        Assert.Equal(second.Id, (await patients.GetByStationAsync(second.Id))!.CurrentStationId);

        var patientTwo = await service.AddPatientAsync(first.Id, "Resus");
        await service.MovePatientAsync(first.Id, second.Id, true);
        Assert.Equal(patientOne.Uid, (await patients.GetByStationAsync(first.Id))!.Uid);
        Assert.Equal(patientTwo.Uid, (await patients.GetByStationAsync(second.Id))!.Uid);
        Assert.Equal(5, (await patients.GetAllEventsAsync()).Count);
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
        private readonly List<PatientEvent> _events = [];

        public Task<IReadOnlyList<Patient>> GetAllAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<Patient>>(_patients.ToList());
        public Task<int> GetNextPatientNumberAsync(CancellationToken cancellationToken = default) => Task.FromResult(_patients.Count + 1);
        public Task<Patient?> GetByStationAsync(Guid stationId, CancellationToken cancellationToken = default) => Task.FromResult(_patients.FirstOrDefault(patient => patient.CurrentStationId == stationId));
        public Task AddAsync(Patient patient, CancellationToken cancellationToken = default) { _patients.Add(patient); return Task.CompletedTask; }
        public Task<Patient?> DischargeFromStationAsync(Guid stationId, DateTimeOffset dischargedAt, string? dischargeRoute, CancellationToken cancellationToken = default)
        {
            for (var index = 0; index < _patients.Count; index++)
            {
                if (_patients[index].CurrentStationId == stationId)
                {
                    _patients[index] = _patients[index] with { CurrentStationId = null, DischargedAt = dischargedAt, DischargeRoute = dischargeRoute };
                    return Task.FromResult<Patient?>(_patients[index]);
                }
            }

            return Task.FromResult<Patient?>(null);
        }

        public Task<PatientTransferResult> MoveAsync(Guid sourceStationId, Guid destinationStationId, bool swap, CancellationToken cancellationToken = default)
        {
            var sourceIndex = _patients.FindIndex(patient => patient.CurrentStationId == sourceStationId);
            var destinationIndex = _patients.FindIndex(patient => patient.CurrentStationId == destinationStationId);
            if (sourceIndex < 0) throw new InvalidOperationException();
            if (destinationIndex >= 0 && !swap) throw new InvalidOperationException();
            var source = _patients[sourceIndex] with { CurrentStationId = destinationStationId };
            _patients[sourceIndex] = source;
            Patient? destination = null;
            if (destinationIndex >= 0)
            {
                destination = _patients[destinationIndex] with { CurrentStationId = sourceStationId };
                _patients[destinationIndex] = destination;
            }
            return Task.FromResult(new PatientTransferResult(source, destination));
        }

        public Task AddEventAsync(PatientEvent patientEvent, CancellationToken cancellationToken = default) { _events.Add(patientEvent); return Task.CompletedTask; }
        public Task<IReadOnlyList<PatientEvent>> GetAllEventsAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<PatientEvent>>(_events.ToList());
    }
}
