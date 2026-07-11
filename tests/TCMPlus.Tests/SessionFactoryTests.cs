using TCMPlus.Infrastructure.Sessions;

namespace TCMPlus.Tests;

public sealed class SessionFactoryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "TCMPlus.Tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void Creates_a_distinct_directory_for_each_session()
    {
        var factory = new SessionFactory(_root);

        var first = factory.CreateNewSession("Night shift");
        var second = factory.CreateNewSession("Night shift");

        Assert.NotEqual(first.Id, second.Id);
        Assert.NotEqual(first.DirectoryPath, second.DirectoryPath);
        Assert.True(Directory.Exists(first.DirectoryPath));
        Assert.True(Directory.Exists(second.DirectoryPath));
        Assert.EndsWith("tcm.sqlite", first.DatabasePath, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("Night shift", first.ShiftName);
        Assert.Contains("Night-shift", first.DirectoryPath, StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, true);
        }
    }
}
