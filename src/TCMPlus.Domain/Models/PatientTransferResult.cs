namespace TCMPlus.Domain.Models;

public sealed record PatientTransferResult(Patient SourcePatient, Patient? SwappedPatient);
