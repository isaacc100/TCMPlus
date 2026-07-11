namespace TCMPlus.Domain.Models;

public sealed record TcSessionSettings(string? ShiftName, string? PinSalt, string? PinHash)
{
    public bool HasShiftPin => !string.IsNullOrWhiteSpace(PinSalt) && !string.IsNullOrWhiteSpace(PinHash);
}
