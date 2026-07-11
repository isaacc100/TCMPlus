namespace TCMPlus.Domain.Models;

public sealed record Patient(
    Guid Uid,
    DateTimeOffset AddedAt,
    Guid? CurrentStationId);
