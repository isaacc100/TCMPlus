using TCMPlus.Domain.Models;
using TCMPlus.Domain.Persistence;
using TCMPlus.Domain.Services;

namespace TCMPlus.Infrastructure.Services;

public sealed class TreatmentCentreLayoutService(
    IStationRepository stationRepository,
    IPatientRepository patientRepository,
    ITcSettingsRepository settingsRepository,
    ITreatmentCentreLayoutRepository layoutRepository) : ITreatmentCentreLayoutService
{
    public async Task<TreatmentCentreLayout> LoadAsync(CancellationToken cancellationToken = default) =>
        new(
            await stationRepository.GetAllAsync(cancellationToken),
            (await settingsRepository.GetAsync(cancellationToken)).GridDensity);

    public async Task CommitAsync(TreatmentCentreLayout layout, CancellationToken cancellationToken = default)
    {
        var current = await LoadAsync(cancellationToken);
        ValidateDensity(current.GridDensity, layout.GridDensity);

        var normalized = layout.Stations.Select(NormalizeAndValidateStation).ToList();
        if (normalized.Select(station => station.Id).Distinct().Count() != normalized.Count)
        {
            throw new InvalidOperationException("The treatment-centre layout contains a duplicate station.");
        }

        ValidateBoundsAndOverlap(normalized, layout.GridDensity);

        var retainedIds = normalized.Select(station => station.Id).ToHashSet();
        var removedIds = current.Stations.Select(station => station.Id).Where(id => !retainedIds.Contains(id)).ToHashSet();
        var occupiedRemoved = (await patientRepository.GetAllActiveAsync(cancellationToken))
            .FirstOrDefault(patient => patient.CurrentStationId is Guid stationId && removedIds.Contains(stationId));
        if (occupiedRemoved is not null)
        {
            throw new InvalidOperationException("Transfer or discharge the current patient before deleting this station.");
        }

        await layoutRepository.CommitAsync(
            new TreatmentCentreLayout(normalized, layout.GridDensity),
            DateTimeOffset.UtcNow,
            cancellationToken);
    }

    private static Station NormalizeAndValidateStation(Station station)
    {
        var name = station.Name.Trim();
        if (name.Length == 0)
        {
            throw new InvalidOperationException("Stations require a name.");
        }

        if (station.GridWidth < 7 || station.GridHeight < 7)
        {
            throw new InvalidOperationException("Stations must be at least 7 by 7 grid units.");
        }

        return station with { Name = name, Type = station.Type.Trim() };
    }

    private static void ValidateDensity(GridDensity current, GridDensity requested)
    {
        if (DensityRank(requested) < DensityRank(current))
        {
            throw new InvalidOperationException("The map can be enlarged, but cannot be reduced after it has been created.");
        }
    }

    private static int DensityRank(GridDensity density) => density switch
    {
        GridDensity.Compact => 0,
        GridDensity.Standard => 1,
        GridDensity.Dense => 2,
        _ => throw new InvalidOperationException("Choose a supported map density.")
    };

    private static void ValidateBoundsAndOverlap(IReadOnlyList<Station> stations, GridDensity density)
    {
        var (columns, rows) = density switch
        {
            GridDensity.Compact => (50d, 30d),
            GridDensity.Standard => (60d, 36d),
            GridDensity.Dense => (75d, 45d),
            _ => throw new InvalidOperationException("Choose a supported map density.")
        };

        foreach (var station in stations)
        {
            if (station.GridX < 0 || station.GridY < 0
                || station.GridX + station.GridWidth > columns
                || station.GridY + station.GridHeight > rows)
            {
                throw new InvalidOperationException($"{station.Name} is outside the treatment-centre map.");
            }
        }

        for (var firstIndex = 0; firstIndex < stations.Count; firstIndex++)
        {
            for (var secondIndex = firstIndex + 1; secondIndex < stations.Count; secondIndex++)
            {
                if (Intersects(stations[firstIndex], stations[secondIndex]))
                {
                    throw new InvalidOperationException($"{stations[firstIndex].Name} overlaps {stations[secondIndex].Name}.");
                }
            }
        }
    }

    private static bool Intersects(Station first, Station second) =>
        first.GridX < second.GridX + second.GridWidth
        && first.GridX + first.GridWidth > second.GridX
        && first.GridY < second.GridY + second.GridHeight
        && first.GridY + first.GridHeight > second.GridY;
}
