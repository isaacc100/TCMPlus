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

        await service.DischargePatientAsync(station.Id, "Conveyed", null);
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
        await service.DischargePatientAsync(station.Id, "Conveyed", "See, Treat, Discharge");
        var active = await service.AddPatientAsync(station.Id, null);

        var allPatients = await service.GetPatientsAsync();
        Assert.Equal([discharged.Uid, active.Uid], allPatients.Select(patient => patient.Uid));

        var dischargedRecord = allPatients.Single(patient => patient.Uid == discharged.Uid);
        var correctedAddedAt = dischargedRecord.AddedAt.AddMinutes(-5);
        var correctedDischargedAt = dischargedRecord.DischargedAt!.Value.AddMinutes(5);
        var updated = await service.UpdatePatientDetailsAsync(discharged.Uid, correctedAddedAt, correctedDischargedAt, "  Corrected complaint  ", "  Self-care  ", "See, Advice-only, Discharge");
        Assert.Equal(correctedAddedAt, updated.AddedAt);
        Assert.Equal(correctedDischargedAt, updated.DischargedAt);
        Assert.Equal("Corrected complaint", updated.PresentingComplaint);
        Assert.Equal("Self-care", updated.DischargeRoute);
        Assert.Equal("See, Advice-only, Discharge", updated.DischargeOutcome);

        updated = await service.UpdatePatientDetailsAsync(discharged.Uid, correctedAddedAt, correctedDischargedAt, "   ", "   ", "   ");
        Assert.Null(updated.PresentingComplaint);
        Assert.Null(updated.DischargeRoute);
        Assert.Null(updated.DischargeOutcome);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.UpdatePatientDetailsAsync(discharged.Uid, correctedDischargedAt, correctedDischargedAt, null, null, null));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.UpdatePatientDetailsAsync(active.Uid, active.AddedAt, null, null, "Conveyed", null));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.UpdatePatientDetailsAsync(Guid.NewGuid(), DateTimeOffset.UtcNow, null, null, null, null));

        await service.UpdatePresentingComplaintAsync([discharged.Uid, active.Uid], "  Shared complaint  ");
        Assert.All(await service.GetPatientsAsync(), patient => Assert.Equal("Shared complaint", patient.PresentingComplaint));
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

    [Fact]
    public async Task Reorders_only_the_complete_unique_station_list()
    {
        var first = new Station(Guid.NewGuid(), "Bay 1", "Bed", 1, 1, 8, 7);
        var second = new Station(Guid.NewGuid(), "Bay 2", "Bed", 10, 1, 8, 7);
        var stations = new InMemoryStationRepository(first, second);
        var service = new TreatmentCentreService(stations, new InMemoryPatientRepository());

        await service.ReorderStationsAsync([second.Id, first.Id]);

        Assert.Equal([second.Id, first.Id], (await stations.GetAllAsync()).Select(station => station.Id));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ReorderStationsAsync([first.Id]));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ReorderStationsAsync([first.Id, first.Id]));
    }

    [Fact]
    public async Task Mobile_team_lifecycle_validates_callsigns_and_clears_location_on_stand_down()
    {
        var teams = new InMemoryMobileTeamRepository();
        var service = new TreatmentCentreService(new InMemoryStationRepository(), new InMemoryPatientRepository(), teams);

        var team = await service.AddMobileTeamAsync("  RESPONSE 3  ", "  Pat / Morgan  ");
        Assert.Equal("RESPONSE 3", team.Callsign);
        Assert.Equal("Pat / Morgan", team.Note);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.AddMobileTeamAsync("response 3", null));

        team = await service.DeployMobileTeamAsync(team.Id, "  West entrance  ");
        Assert.True(team.IsDeployed);
        Assert.Equal("West entrance", team.DeploymentLocation);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.DeleteMobileTeamAsync(team.Id));

        team = await service.UpdateMobileTeamLocationAsync(team.Id, " ");
        Assert.Null(team.DeploymentLocation);
        team = await service.StandDownMobileTeamAsync(team.Id);
        Assert.False(team.IsDeployed);
        Assert.Null(team.DeploymentLocation);

        await service.DeleteMobileTeamAsync(team.Id);
        Assert.Empty(await service.GetMobileTeamsAsync());
    }

    [Fact]
    public async Task Mobile_team_patients_count_immediately_and_transfer_only_to_valid_empty_assignments()
    {
        var firstStation = new Station(Guid.NewGuid(), "Bay 1", "Bed", 1, 1, 8, 7);
        var secondStation = new Station(Guid.NewGuid(), "Bay 2", "Bed", 10, 1, 8, 7);
        var patients = new InMemoryPatientRepository();
        var teams = new InMemoryMobileTeamRepository();
        var service = new TreatmentCentreService(new InMemoryStationRepository(firstStation, secondStation), patients, teams);
        var team = await service.AddMobileTeamAsync("DELTA 1", null);

        var mobilePatient = await service.AddPatientToMobileTeamAsync(team.Id, null);
        Assert.Equal(team.Id, mobilePatient.CurrentMobileTeamId);
        Assert.Equal(1, await service.GetPatientsSeenThisShiftAsync());
        Assert.Equal(0, (await service.GetDashboardAsync()).OccupiedStations);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.AddPatientToMobileTeamAsync(team.Id, null));

        await service.MovePatientAsync(mobilePatient.Uid, new PatientAssignment(PatientAssignmentKind.Station, firstStation.Id), false);
        Assert.Equal(firstStation.Id, (await patients.GetByUidAsync(mobilePatient.Uid))!.CurrentStationId);
        Assert.Equal(1, (await service.GetDashboardAsync()).Occupancy[^1].OccupiedStations);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.MovePatientAsync(mobilePatient.Uid, new PatientAssignment(PatientAssignmentKind.MobileTeam, team.Id), false));
        team = await service.DeployMobileTeamAsync(team.Id, null);
        await service.MovePatientAsync(mobilePatient.Uid, new PatientAssignment(PatientAssignmentKind.MobileTeam, team.Id), false);
        Assert.Equal(team.Id, (await patients.GetByUidAsync(mobilePatient.Uid))!.CurrentMobileTeamId);
        Assert.Equal(0, (await service.GetDashboardAsync()).Occupancy[^1].OccupiedStations);

        var stationPatient = await service.AddPatientAsync(secondStation.Id, null);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.MovePatientAsync(stationPatient.Uid, new PatientAssignment(PatientAssignmentKind.MobileTeam, team.Id), false));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.StandDownMobileTeamAsync(team.Id));

        await service.DischargeAssignedPatientAsync(mobilePatient.Uid, "Home", null);
        team = await service.StandDownMobileTeamAsync(team.Id);
        Assert.False(team.IsDeployed);
    }

    private sealed class InMemoryStationRepository(params Station[] stations) : IStationRepository
    {
        private readonly List<Station> _stations = [.. stations];

        public Task<IReadOnlyList<Station>> GetAllAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<Station>>(_stations);
        public Task AddAsync(Station station, CancellationToken cancellationToken = default) { _stations.Add(station); return Task.CompletedTask; }
        public Task UpdateAsync(Station station, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task UpdateOrderAsync(IReadOnlyList<Guid> stationIds, CancellationToken cancellationToken = default)
        {
            var byId = _stations.ToDictionary(station => station.Id);
            _stations.Clear();
            _stations.AddRange(stationIds.Select(stationId => byId[stationId]));
            return Task.CompletedTask;
        }
        public Task SoftDeleteAsync(Guid stationId, DateTimeOffset deletedAt, CancellationToken cancellationToken = default) { _stations.RemoveAll(item => item.Id == stationId); return Task.CompletedTask; }
    }

    private sealed class InMemoryMobileTeamRepository : IMobileTeamRepository
    {
        private readonly List<MobileTeam> _teams = [];
        public Task<IReadOnlyList<MobileTeam>> GetAllAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<MobileTeam>>(_teams.OrderBy(team => team.Callsign, StringComparer.OrdinalIgnoreCase).ToList());
        public Task<MobileTeam?> GetByIdAsync(Guid teamId, CancellationToken cancellationToken = default) =>
            Task.FromResult(_teams.FirstOrDefault(team => team.Id == teamId));
        public Task AddAsync(MobileTeam team, CancellationToken cancellationToken = default) { _teams.Add(team); return Task.CompletedTask; }
        public Task UpdateAsync(MobileTeam team, CancellationToken cancellationToken = default)
        {
            var index = _teams.FindIndex(item => item.Id == team.Id);
            if (index >= 0) _teams[index] = team;
            return Task.CompletedTask;
        }
        public Task SoftDeleteAsync(Guid teamId, DateTimeOffset deletedAt, CancellationToken cancellationToken = default)
        {
            _teams.RemoveAll(team => team.Id == teamId);
            return Task.CompletedTask;
        }
    }

    private sealed class InMemoryPatientRepository : IPatientRepository
    {
        private readonly List<Patient> _patients = [];

        public Task<IReadOnlyList<Patient>> GetAllActiveAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<Patient>>(_patients.Where(patient => patient.DischargedAt is null).ToList());
        private readonly List<PatientEvent> _events = [];

        public Task<IReadOnlyList<Patient>> GetAllAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<Patient>>(_patients.ToList());
        public Task<int> GetNextPatientNumberAsync(CancellationToken cancellationToken = default) => Task.FromResult(_patients.Count + 1);
        public Task<Patient?> GetByUidAsync(Guid patientUid, CancellationToken cancellationToken = default) => Task.FromResult(_patients.FirstOrDefault(patient => patient.Uid == patientUid));
        public Task<Patient?> GetByStationAsync(Guid stationId, CancellationToken cancellationToken = default) => Task.FromResult(_patients.FirstOrDefault(patient => patient.CurrentStationId == stationId));
        public Task<Patient?> GetByMobileTeamAsync(Guid teamId, CancellationToken cancellationToken = default) => Task.FromResult(_patients.FirstOrDefault(patient => patient.CurrentMobileTeamId == teamId));
        public Task AddAsync(Patient patient, CancellationToken cancellationToken = default) { _patients.Add(patient); return Task.CompletedTask; }
        public Task UpdateDetailsAsync(Patient patient, CancellationToken cancellationToken = default)
        {
            var index = _patients.FindIndex(item => item.Uid == patient.Uid);
            if (index >= 0) _patients[index] = patient;
            return Task.CompletedTask;
        }
        public Task UpdatePresentingComplaintAsync(IReadOnlyCollection<Guid> patientUids, string presentingComplaint, CancellationToken cancellationToken = default)
        {
            for (var index = 0; index < _patients.Count; index++)
            {
                if (patientUids.Contains(_patients[index].Uid))
                {
                    _patients[index] = _patients[index] with { PresentingComplaint = presentingComplaint };
                }
            }
            return Task.CompletedTask;
        }
        public Task DeleteAsync(Guid patientUid, CancellationToken cancellationToken = default)
        {
            _patients.RemoveAll(patient => patient.Uid == patientUid);
            _events.RemoveAll(patientEvent => patientEvent.PatientUid == patientUid);
            return Task.CompletedTask;
        }
        public Task<Patient?> DischargeFromStationAsync(Guid stationId, DateTimeOffset dischargedAt, string? dischargeRoute, string? dischargeOutcome, CancellationToken cancellationToken = default)
        {
            for (var index = 0; index < _patients.Count; index++)
            {
                if (_patients[index].CurrentStationId == stationId)
                {
                    _patients[index] = _patients[index] with { CurrentStationId = null, DischargedAt = dischargedAt, DischargeRoute = dischargeRoute, DischargeOutcome = dischargeOutcome };
                    return Task.FromResult<Patient?>(_patients[index]);
                }
            }

            return Task.FromResult<Patient?>(null);
        }

        public Task<Patient?> DischargeAsync(Guid patientUid, DateTimeOffset dischargedAt, string? dischargeRoute, string? dischargeOutcome, CancellationToken cancellationToken = default)
        {
            var index = _patients.FindIndex(patient => patient.Uid == patientUid && patient.DischargedAt is null);
            if (index < 0) return Task.FromResult<Patient?>(null);
            _patients[index] = _patients[index] with
            {
                CurrentStationId = null,
                CurrentMobileTeamId = null,
                DischargedAt = dischargedAt,
                DischargeRoute = dischargeRoute,
                DischargeOutcome = dischargeOutcome
            };
            return Task.FromResult<Patient?>(_patients[index]);
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

        public Task<PatientTransferResult> MoveAsync(Guid patientUid, PatientAssignment destination, bool swap, CancellationToken cancellationToken = default)
        {
            var sourceIndex = _patients.FindIndex(patient => patient.Uid == patientUid);
            if (sourceIndex < 0) throw new InvalidOperationException();
            var source = _patients[sourceIndex];
            var sourceAssignment = source.CurrentStationId is Guid stationId
                ? new PatientAssignment(PatientAssignmentKind.Station, stationId)
                : new PatientAssignment(PatientAssignmentKind.MobileTeam, source.CurrentMobileTeamId!.Value);
            var destinationIndex = _patients.FindIndex(patient => destination.Kind == PatientAssignmentKind.Station
                ? patient.CurrentStationId == destination.Id
                : patient.CurrentMobileTeamId == destination.Id);
            if (destinationIndex >= 0 && !swap) throw new InvalidOperationException();

            _patients[sourceIndex] = Assign(source, destination);
            Patient? swapped = null;
            if (destinationIndex >= 0)
            {
                swapped = Assign(_patients[destinationIndex], sourceAssignment);
                _patients[destinationIndex] = swapped;
            }
            return Task.FromResult(new PatientTransferResult(_patients[sourceIndex], swapped));
        }

        public Task AddEventAsync(PatientEvent patientEvent, CancellationToken cancellationToken = default) { _events.Add(patientEvent); return Task.CompletedTask; }
        public Task<IReadOnlyList<PatientEvent>> GetAllEventsAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<PatientEvent>>(_events.ToList());

        private static Patient Assign(Patient patient, PatientAssignment assignment) =>
            assignment.Kind == PatientAssignmentKind.Station
                ? patient with { CurrentStationId = assignment.Id, CurrentMobileTeamId = null }
                : patient with { CurrentStationId = null, CurrentMobileTeamId = assignment.Id };
    }
}
