namespace TCMPlus.Domain.Models;

public sealed record MobileTeamSnapshot(MobileTeam Team, Patient? CurrentPatient);
