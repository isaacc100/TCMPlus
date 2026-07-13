using TCMPlus.Domain.Models;
using TCMPlus.Domain.Persistence;
using TCMPlus.Domain.Services;

namespace TCMPlus.App.LanDisplay;

public sealed class LanDisplaySnapshotProvider
{
    private readonly ITreatmentCentreService _treatmentCentreService;
    private readonly ITcSettingsRepository _settingsRepository;

    public LanDisplaySnapshotProvider(ITreatmentCentreService treatmentCentreService, ITcSettingsRepository settingsRepository)
    {
        _treatmentCentreService = treatmentCentreService;
        _settingsRepository = settingsRepository;
    }

    public async Task<LanDisplaySnapshot> GetAsync(CancellationToken cancellationToken = default)
    {
        var stationsTask = _treatmentCentreService.GetSnapshotAsync(cancellationToken);
        var dashboardTask = _treatmentCentreService.GetDashboardAsync(cancellationToken);
        var settingsTask = _settingsRepository.GetAsync(cancellationToken);
        await Task.WhenAll(stationsTask, dashboardTask, settingsTask);

        var dashboard = await dashboardTask;
        var settings = await settingsTask;
        var stations = (await stationsTask)
            .Select(item => new LanStationSnapshot(
                item.Station.Name,
                item.Station.Type,
                item.Station.GridX,
                item.Station.GridY,
                item.Station.GridWidth,
                item.Station.GridHeight,
                item.CurrentPatient is not null,
                item.CurrentPatient?.PatientNumber,
                item.CurrentPatient?.AddedAt))
            .ToList();

        return new LanDisplaySnapshot(
            DateTimeOffset.Now,
            new LanDashboardSnapshot(
                dashboard.AvailableStations,
                dashboard.OccupiedStations,
                dashboard.PatientsSeen,
                FormatDuration(dashboard.AverageDischargeDuration),
                dashboard.AvailableStations + dashboard.OccupiedStations,
                dashboard.Occupancy.Select(point => new LanChartPoint(point.ObservedAt.LocalDateTime.ToString("HH:mm"), point.OccupiedStations)).ToList(),
                dashboard.CumulativeArrivals.Select(point => new LanChartPoint(point.ObservedAt.LocalDateTime.ToString("HH:mm"), point.PatientsSeen)).ToList()),
            GridSizePixels(settings.GridDensity),
            stations);
    }

    private static double GridSizePixels(GridDensity density) => density switch
    {
        GridDensity.Standard => 20d,
        GridDensity.Dense => 16d,
        _ => 24d
    };

    private static string FormatDuration(TimeSpan? value) => value is null
        ? "No discharges yet"
        : value.Value.TotalHours >= 1
            ? $"{(int)value.Value.TotalHours}h {value.Value.Minutes}m"
            : $"{Math.Max(1, value.Value.Minutes)}m";
}

public sealed record LanDisplaySnapshot(
    DateTimeOffset GeneratedAt,
    LanDashboardSnapshot Dashboard,
    double GridSizePixels,
    IReadOnlyList<LanStationSnapshot> Stations);

public sealed record LanDashboardSnapshot(
    int AvailableStations,
    int OccupiedStations,
    int PatientsSeenThisShift,
    string AverageDischargeText,
    int TotalStations,
    IReadOnlyList<LanChartPoint> Occupancy,
    IReadOnlyList<LanChartPoint> CumulativeArrivals);

public sealed record LanChartPoint(string Label, double Value);

public sealed record LanStationSnapshot(
    string Name,
    string Type,
    double GridX,
    double GridY,
    double GridWidth,
    double GridHeight,
    bool IsOccupied,
    int? PatientNumber,
    DateTimeOffset? AddedAt);
