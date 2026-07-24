namespace TCMPlus.Domain.Models;

public static class DischargeOutcomeOptions
{
    public static IReadOnlyList<string> Defaults { get; } =
    [
        "See, Treat, Discharge",
        "See, Advice-only, Discharge"
    ];
}
