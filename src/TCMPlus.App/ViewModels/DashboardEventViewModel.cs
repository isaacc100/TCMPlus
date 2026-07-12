using TCMPlus.Domain.Models;

namespace TCMPlus.App.ViewModels;

public sealed record DashboardEventViewModel(string Title, string Detail, string Time)
{
    public static DashboardEventViewModel FromEvent(PatientEvent item) => item.Type switch
    {
        PatientEventType.Added => new($"Patient {item.PatientNumber} added", $"Assigned to {item.ToStationName}", Format(item.OccurredAt)),
        PatientEventType.Discharged => new($"Patient {item.PatientNumber} discharged", $"Left {item.FromStationName}", Format(item.OccurredAt)),
        _ => new($"Patient {item.PatientNumber} moved", $"{item.FromStationName} → {item.ToStationName}", Format(item.OccurredAt))
    };

    private static string Format(DateTimeOffset occurredAt)
    {
        var elapsed = DateTimeOffset.UtcNow - occurredAt;
        return elapsed < TimeSpan.FromMinutes(1) ? "now" : elapsed < TimeSpan.FromHours(1) ? $"{Math.Max(1, (int)elapsed.TotalMinutes)}m ago" : occurredAt.LocalDateTime.ToString("HH:mm");
    }
}

public sealed record DashboardChartSlice(string Label, int Value, string Color);
public sealed record DashboardChartPoint(string Label, double Value);
