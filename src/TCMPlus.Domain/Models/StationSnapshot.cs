namespace TCMPlus.Domain.Models;

public sealed record StationSnapshot(Station Station, Patient? CurrentPatient);
