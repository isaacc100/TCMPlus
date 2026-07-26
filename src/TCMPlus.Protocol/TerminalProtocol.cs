using System.Text.Json.Serialization;

namespace TCMPlus.Protocol;

public static class TerminalProtocol
{
    public const int CurrentVersion = 1;
    public const string VersionHeader = "X-TCM-Protocol";
    public const string ApiRoot = "/api/terminal/v1";
}

public sealed record TerminalLoginRequest(string TerminalName, string Password, int ProtocolVersion);
public sealed record TerminalLoginResponse(
    string AccessToken,
    DateTimeOffset ExpiresAt,
    Guid TerminalId,
    string TerminalName,
    string ShiftName,
    int ProtocolVersion);

public sealed record TerminalApiError(string Code, string Message, Guid? RequestId = null);

public sealed record TerminalSnapshotResponse(
    long Sequence,
    DateTimeOffset GeneratedAt,
    string ShiftName,
    TerminalGridDensity GridDensity,
    bool QuickEntry,
    IReadOnlyList<string> DischargeRoutes,
    IReadOnlyList<TerminalStation> Stations,
    IReadOnlyList<TerminalMobileTeam> MobileTeams,
    TerminalDashboard Dashboard);

public sealed record TerminalStation(
    Guid Id,
    string Name,
    string Type,
    double GridX,
    double GridY,
    double GridWidth,
    double GridHeight,
    TerminalPatient? Patient);

public sealed record TerminalMobileTeam(
    Guid Id,
    string Callsign,
    string? Note,
    bool IsDeployed,
    string? DeploymentLocation,
    TerminalPatient? Patient);

public sealed record TerminalPatient(Guid Reference, int Number, DateTimeOffset AddedAt);

public sealed record TerminalDashboard(
    int AvailableStations,
    int OccupiedStations,
    int PatientsSeen,
    long? AverageDischargeTicks,
    IReadOnlyList<TerminalChartPoint> Occupancy,
    IReadOnlyList<TerminalChartPoint> CumulativeArrivals);

public sealed record TerminalChartPoint(DateTimeOffset ObservedAt, double Value);

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TerminalGridDensity { Compact, Standard, Dense }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TerminalCommandKind
{
    AddPatientToStation,
    AddPatientToMobileTeam,
    MovePatient,
    DischargePatient,
    AddMobileTeam,
    UpdateMobileTeam,
    DeployMobileTeam,
    UpdateMobileTeamLocation,
    StandDownMobileTeam
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TerminalAssignmentKind { Station, MobileTeam }

public sealed record TerminalCommandRequest(
    Guid RequestId,
    TerminalCommandKind Kind,
    long? ExpectedSequence = null,
    Guid? TargetId = null,
    Guid? SecondaryId = null,
    TerminalAssignmentKind? DestinationKind = null,
    bool Swap = false,
    string? Name = null,
    string? Note = null,
    string? Location = null,
    string? DischargeRoute = null,
    string? DischargeOutcome = null,
    DateTimeOffset? CreatedAt = null);

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TerminalCommandStatus { Accepted, Rejected }

public sealed record TerminalCommandResponse(
    Guid RequestId,
    TerminalCommandStatus Status,
    long Sequence,
    DateTimeOffset ProcessedAt,
    string? ErrorCode = null,
    string? Message = null);

public sealed record TerminalRegistration(
    Guid Id,
    string Name,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt,
    DateTimeOffset? RevokedAt,
    int ProtocolVersion)
{
    public bool IsActive => RevokedAt is null && ExpiresAt > DateTimeOffset.UtcNow;
}

public sealed record TerminalCredential(TerminalRegistration Registration, string Password);

public sealed record TerminalAuditEntry(
    long Sequence,
    Guid RequestId,
    Guid TerminalId,
    string TerminalName,
    DateTimeOffset ProcessedAt,
    TerminalCommandKind Operation,
    string? Target,
    TerminalCommandStatus Status,
    string? RejectionReason);
