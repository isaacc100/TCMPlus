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

    [Fact]
    public async Task Returns_all_patients_and_allows_only_discharged_patient_detail_corrections()
    {
        var station = new Station(Guid.NewGuid(), "Bay 1", "Bed", 1, 1, 8, 7);
        var patients = new InMemoryPatientRepository();
        var service = new TreatmentCentreService(new InMemoryStationRepository(station), patients);
        var discharged = await service.AddPatientAsync(station.Id, "Initial complaint");
        await service.DischargePatientAsync(station.Id, "Conveyed");
        var active = await service.AddPatientAsync(station.Id, null);

        var allPatients = await service.GetPatientsAsync();
        Assert.Equal([discharged.Uid, active.Uid], allPatients.Select(patient => patient.Uid));

        var updated = await service.UpdatePatientDetailsAsync(discharged.Uid, "  Corrected complaint  ", "  Self-care  ");
        Assert.Equal("Corrected complaint", updated.PresentingComplaint);
        Assert.Equal("Self-care", updated.DischargeRoute);

        updated = await service.UpdatePatientDetailsAsync(discharged.Uid, "   ", "   ");
        Assert.Null(updated.PresentingComplaint);
        Assert.Null(updated.DischargeRoute);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.UpdatePatientDetailsAsync(active.Uid, null, "Conveyed"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.UpdatePatientDetailsAsync(Guid.NewGuid(), null, null));
    }

    [Fact]
    public async Task Dashboard_exposes_occupancy_and_cumulative_arrivals_in_fifteen_minute_intervals()
    {
        var station = new Station(Guid.NewGuid(), "Bay 1", "Bed", 1, 1, 8, 7);
        var patients = new InMemoryPatientRepository();
        var now = DateTimeOffset.UtcNow;
        var start = new DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, now.Minute / 15 * 15, 0, TimeSpan.Zero).AddMinutes(-30);
        await patients.AddAsync(new Patient(Guid.NewGuid(), 1, start.AddMinutes(1), station.Id, null, null, null));
        await patients.AddAsync(new Patient(Guid.NewGuid(), 2, start.AddMinutes(16), station.Id, null, start.AddMinutes(26), "Conveyed"));
        await patients.AddAsync(new Patient(Guid.NewGuid(), 3, start.AddMinutes(2), station.Id, null, start.AddMinutes(5), null));
        var service = new TreatmentCentreService(new InMemoryStationRepository(station), patients);

        var dashboard = await service.GetDashboardAsync();

        Assert.NotEmpty(dashboard.Occupancy);
        Assert.NotEmpty(dashboard.CumulativeArrivals);
        var route = Assert.Single(dashboard.DischargeRouteBreakdown);
        Assert.Equal("Conveyed", route.Route);
        Assert.Equal(1, route.Count);
        Assert.Equal(3, dashboard.CumulativeArrivals[^1].PatientsSeen);
        Assert.Equal(1, dashboard.Occupancy[^1].OccupiedStations);
        var intervals = dashboard.CumulativeArrivals.Zip(dashboard.CumulativeArrivals.Skip(1)).Select(pair => pair.Second.ObservedAt - pair.First.ObservedAt).ToList();
        Assert.All(intervals, interval => Assert.InRange(interval, TimeSpan.FromTicks(1), TimeSpan.FromMinutes(15)));
        Assert.Contains(TimeSpan.FromMinutes(15), intervals);
    }

    [Fact]
    public async Task Deletes_patients_and_only_deletes_unoccupied_stations()
    {
        var station = new Station(Guid.NewGuid(), "Bay 1", "Bed", 1, 1, 8, 7);
        var stations = new InMemoryStationRepository(station);
        var patients = new InMemoryPatientRepository();
        var service = new TreatmentCentreService(stations, patients);
        var patient = await service.AddPatientAsync(station.Id, null);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.DeleteStationAsync(station.Id));
        await service.DeletePatientAsync(patient.Uid);

        Assert.Empty(await service.GetPatientsAsync());
        Assert.Empty(await patients.GetAllEventsAsync());
        Assert.Null(Assert.Single(await service.GetSnapshotAsync()).CurrentPatient);

        await service.DeleteStationAsync(station.Id);
        Assert.Empty(await service.GetSnapshotAsync());
    }

    private sealed class InMemoryStationRepository(params Station[] stations) : IStationRepository
    {
        private readonly List<Station> _stations = [.. stations];

        public Task<IReadOnlyList<Station>> GetAllAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<Station>>(_stations);
        public Task AddAsync(Station station, CancellationToken cancellationToken = default) { _stations.Add(station); return Task.CompletedTask; }
        public Task UpdateAsync(Station station, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SoftDeleteAsync(Guid stationId, DateTimeOffset deletedAt, CancellationToken cancellationToken = default) { _stations.RemoveAll(item => item.Id == stationId); return Task.CompletedTask; }
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
        public Task UpdateDetailsAsync(Patient patient, CancellationToken cancellationToken = default)
        {
            var index = _patients.FindIndex(item => item.Uid == patient.Uid);
            if (index >= 0) _patients[index] = patient;
            return Task.CompletedTask;
        }
        public Task DeleteAsync(Guid patientUid, CancellationToken cancellationToken = default)
        {
            _patients.RemoveAll(patient => patient.Uid == patientUid);
            _events.RemoveAll(patientEvent => patientEvent.PatientUid == patientUid);
            return Task.CompletedTask;
        }
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
