namespace TCMPlus.Domain.Models;

public sealed record TcSessionSettings(string? ShiftName, string? PinSalt, string? PinHash, bool QuickEntry = false, GridDensity GridDensity = GridDensity.Compact)
{
    public bool HasShiftPin => !string.IsNullOrWhiteSpace(PinSalt) && !string.IsNullOrWhiteSpace(PinHash);
}

public enum GridDensity { Compact, Standard, Dense }
