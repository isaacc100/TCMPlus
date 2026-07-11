using TCMPlus.Domain.Models;

namespace TCMPlus.Infrastructure.Sessions;

public sealed class SessionFactory
{
    private readonly string _sessionsRoot;

    public SessionFactory(string? applicationDataRoot = null)
    {
        var root = applicationDataRoot
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TCMPlus");

        _sessionsRoot = Path.Combine(root, "Sessions");
    }

    public SessionDescriptor CreateNewSession(string shiftName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(shiftName);
        var id = Guid.NewGuid();
        var startedAt = DateTimeOffset.UtcNow;
        var safeShiftName = ToSafeFolderSegment(shiftName);
        var name = $"{startedAt:yyyyMMdd-HHmmss}-{safeShiftName}-{id:N}";
        var directoryPath = Path.Combine(_sessionsRoot, name);

        Directory.CreateDirectory(directoryPath);

        return new SessionDescriptor(id, startedAt, shiftName.Trim(), directoryPath, Path.Combine(directoryPath, "tcm.sqlite"));
    }

    private static string ToSafeFolderSegment(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var characters = value.Trim()
            .Select(character => invalid.Contains(character) || char.IsWhiteSpace(character) ? '-' : character)
            .ToArray();
        var segment = new string(characters).Trim('-');
        return string.IsNullOrWhiteSpace(segment) ? "shift" : segment;
    }
}
