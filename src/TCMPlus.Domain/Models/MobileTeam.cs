namespace TCMPlus.Domain.Models;

public sealed record MobileTeam(
    Guid Id,
    string Callsign,
    string? Note,
    bool IsDeployed,
    string? DeploymentLocation);
