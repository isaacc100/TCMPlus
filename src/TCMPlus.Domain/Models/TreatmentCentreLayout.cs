namespace TCMPlus.Domain.Models;

public sealed record TreatmentCentreLayout(
    IReadOnlyList<Station> Stations,
    GridDensity GridDensity);
