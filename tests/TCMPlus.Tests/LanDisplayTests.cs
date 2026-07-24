using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using TCMPlus.App.LanDisplay;
using TCMPlus.Domain.Models;
using TCMPlus.Domain.Persistence;
using TCMPlus.Domain.Services;

namespace TCMPlus.Tests;

public sealed class LanDisplayTests
{
    [Fact]
    public async Task Snapshot_preserves_station_location_and_limits_patient_fields()
    {
        var addedAt = DateTimeOffset.UtcNow.AddMinutes(-12);
        var station = new Station(Guid.NewGuid(), "Bay 4", "Bed", 7, 9, 8, 6);
        var patient = new Patient(Guid.NewGuid(), 17, addedAt, station.Id, "Private complaint", null, null);
        var provider = new LanDisplaySnapshotProvider(new FakeTreatmentCentreService(station, patient), new FakeSettingsRepository(GridDensity.Standard));

        var snapshot = await provider.GetAsync();

        Assert.Equal(20, snapshot.GridSizePixels);
        var result = Assert.Single(snapshot.Stations);
        Assert.Equal((7, 9, 8, 6), (result.GridX, result.GridY, result.GridWidth, result.GridHeight));
        Assert.Equal(17, result.PatientNumber);
        Assert.Equal(addedAt, result.AddedAt);
        Assert.DoesNotContain("Uid", JsonSerializer.Serialize(result), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("complaint", JsonSerializer.Serialize(result), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, snapshot.Dashboard.TotalStations);
        Assert.Equal(4, snapshot.Dashboard.Occupancy.Single().Value);
    }

    [Fact]
    public async Task Server_requires_pin_and_stops_serving()
    {
        var station = new Station(Guid.NewGuid(), "Bay 4", "Bed", 7, 9, 8, 6);
        var patient = new Patient(Guid.NewGuid(), 17, DateTimeOffset.UtcNow.AddMinutes(-12), station.Id, null, null, null);
        var provider = new LanDisplaySnapshotProvider(new FakeTreatmentCentreService(station, patient), new FakeSettingsRepository(GridDensity.Dense));
        await using var server = new LanDisplayServer(provider, new LanDisplayServerOptions(IPAddress.Loopback));
        var access = await server.StartAsync();
        Assert.Matches("^[0-9]{4} [0-9]{4}$", access.ViewerPin);
        var dashboardUri = new Uri(Assert.Single(access.Addresses).DashboardUrl);
        var root = new Uri(dashboardUri.GetLeftPart(UriPartial.Authority));
        var cookies = new CookieContainer();
        using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, CookieContainer = cookies }) { BaseAddress = root };

        var response = await client.GetAsync("/dashboard");
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/snapshot")).StatusCode);

        var validPin = access.ViewerPin.Replace(" ", "", StringComparison.Ordinal);
        var invalidPin = validPin == "00000000" ? "11111111" : "00000000";
        response = await client.PostAsync("/login", new FormUrlEncodedContent(new Dictionary<string, string> { ["pin"] = invalidPin, ["returnUrl"] = "/map" }));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("eight-digit viewer PIN", await response.Content.ReadAsStringAsync());

        response = await client.PostAsync("/login", new FormUrlEncodedContent(new Dictionary<string, string> { ["pin"] = validPin, ["returnUrl"] = "/map" }));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/map", response.Headers.Location?.OriginalString);

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/dashboard")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/map")).StatusCode);
        var json = await client.GetFromJsonAsync<JsonElement>("/api/snapshot");
        var stationJson = json.GetProperty("stations")[0];
        Assert.Equal(7, stationJson.GetProperty("gridX").GetDouble());
        Assert.Equal(9, stationJson.GetProperty("gridY").GetDouble());
        Assert.False(stationJson.TryGetProperty("uid", out _));
        Assert.False(stationJson.TryGetProperty("presentingComplaint", out _));

        await server.StopAsync();
        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync("/dashboard"));
    }

    private sealed class FakeSettingsRepository(GridDensity density) : ITcSettingsRepository
    {
        public Task<TcSessionSettings> GetAsync(CancellationToken cancellationToken = default) => Task.FromResult(new TcSessionSettings("Shift", null, null, false, density));
        public Task SaveAsync(TcSessionSettings settings, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class FakeTreatmentCentreService(Station station, Patient patient) : ITreatmentCentreService
    {
        public Task<IReadOnlyList<StationSnapshot>> GetSnapshotAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<StationSnapshot>>([new(station, patient)]);
        public Task<DashboardSnapshot> GetDashboardAsync(CancellationToken cancellationToken = default) => Task.FromResult(new DashboardSnapshot(1, 1, 3, TimeSpan.FromMinutes(24), [], [], [], [], [new(DateTimeOffset.Now, 4)], [new(DateTimeOffset.Now, 3)]));
        public Task<int> GetPatientsSeenThisShiftAsync(CancellationToken cancellationToken = default) => Task.FromResult(3);
        public Task<IReadOnlyList<Patient>> GetPatientsAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<Patient>>([patient]);
        public Task<Station> AddStationAsync(string name, string type, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SaveStationAsync(Station value, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task ReorderStationsAsync(IReadOnlyList<Guid> stationIds, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DeleteStationAsync(Guid stationId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Patient> AddPatientAsync(Guid stationId, string? presentingComplaint, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Patient> UpdatePatientDetailsAsync(Guid patientUid, DateTimeOffset addedAt, DateTimeOffset? dischargedAt, string? presentingComplaint, string? dischargeRoute, string? dischargeOutcome, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task UpdatePresentingComplaintAsync(IReadOnlyCollection<Guid> patientUids, string presentingComplaint, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DeletePatientAsync(Guid patientUid, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DischargePatientAsync(Guid stationId, string? dischargeRoute, string? dischargeOutcome, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<PatientTransferResult> MovePatientAsync(Guid sourceStationId, Guid destinationStationId, bool swap, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
