namespace TCMPlus.Domain.Models;

public sealed record DashboardSnapshot(
    int AvailableStations,
    int OccupiedStations,
    int PatientsSeen,
    TimeSpan? AverageDischargeDuration,
    IReadOnlyList<PatientEvent> RecentEvents,
    IReadOnlyList<ComplaintBreakdown> ComplaintBreakdown,
    IReadOnlyList<ThroughputPoint> Throughput,
    IReadOnlyList<DischargeDurationPoint> DischargeDurations,
    IReadOnlyList<OccupancyPoint> Occupancy,
    IReadOnlyList<CumulativeArrivalPoint> CumulativeArrivals);

public sealed record ComplaintBreakdown(string Complaint, int Count);
public sealed record ThroughputPoint(DateTimeOffset BucketStart, int Discharges);
public sealed record DischargeDurationPoint(DateTimeOffset DischargedAt, TimeSpan Duration);
public sealed record OccupancyPoint(DateTimeOffset ObservedAt, int OccupiedStations);
public sealed record CumulativeArrivalPoint(DateTimeOffset ObservedAt, int PatientsSeen);
