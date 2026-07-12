namespace TCMPlus.Domain.Models;

public sealed record AppSettings(IReadOnlyList<string> DischargeRoutes, ExternalDisplayMode ExternalDisplayMode = ExternalDisplayMode.Dashboard)
{
    public static AppSettings Default { get; } = new(["Non-Conveyed", "Conveyed"]);
}

public enum ExternalDisplayMode { Dashboard, Map }
