namespace TCMPlus.Domain.Models;

public sealed record Patient(
    Guid Uid,
    int PatientNumber,
    DateTimeOffset AddedAt,
    Guid? CurrentStationId,
    string? PresentingComplaint,
    DateTimeOffset? DischargedAt,
    string? DischargeRoute,
    string? DischargeOutcome = null,
    Guid? CurrentMobileTeamId = null);
