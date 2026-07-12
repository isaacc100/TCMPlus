namespace TCMPlus.Domain.Models;

public enum PatientEventType
{
    Added,
    Transferred,
    Discharged
}

public sealed record PatientEvent(
    Guid Id,
    Guid PatientUid,
    int PatientNumber,
    PatientEventType Type,
    DateTimeOffset OccurredAt,
    string? FromStationName,
    string? ToStationName);
