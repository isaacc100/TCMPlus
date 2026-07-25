namespace TCMPlus.Domain.Models;

public enum PatientAssignmentKind
{
    Station,
    MobileTeam
}

public sealed record PatientAssignment(PatientAssignmentKind Kind, Guid Id);
