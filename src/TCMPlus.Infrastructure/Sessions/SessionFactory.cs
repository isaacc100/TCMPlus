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

    public SessionDescriptor CreateNewSession()
    {
        var id = Guid.NewGuid();
        var startedAt = DateTimeOffset.UtcNow;
        var name = $"{startedAt:yyyyMMdd-HHmmss}-{id:N}";
        var directoryPath = Path.Combine(_sessionsRoot, name);

        Directory.CreateDirectory(directoryPath);

        return new SessionDescriptor(id, startedAt, directoryPath, Path.Combine(directoryPath, "tcm.sqlite"));
    }
}
