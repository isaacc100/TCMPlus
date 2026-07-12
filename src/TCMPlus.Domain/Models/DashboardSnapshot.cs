namespace TCMPlus.Domain.Models;

public sealed record DashboardSnapshot(
    int AvailableStations,
    int OccupiedStations,
    int PatientsSeen,
    TimeSpan? AverageDischargeDuration,
    IReadOnlyList<PatientEvent> RecentEvents,
    IReadOnlyList<ComplaintBreakdown> ComplaintBreakdown,
    IReadOnlyList<ThroughputPoint> Throughput,
    IReadOnlyList<DischargeDurationPoint> DischargeDurations);

public sealed record ComplaintBreakdown(string Complaint, int Count);
public sealed record ThroughputPoint(DateTimeOffset BucketStart, int Discharges);
public sealed record DischargeDurationPoint(DateTimeOffset DischargedAt, TimeSpan Duration);
