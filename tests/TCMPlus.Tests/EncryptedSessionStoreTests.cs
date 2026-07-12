using TCMPlus.Infrastructure.Sessions;

namespace TCMPlus.Tests;

public sealed class EncryptedSessionStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "TCMPlusTests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Seals_sessions_to_an_encrypted_tcm_file_that_can_be_reopened()
    {
        var store = new EncryptedSessionStore(_root);
        var session = await store.CreateAsync("Night shift", "password1");
        await File.WriteAllTextAsync(session.DatabasePath, "private session data");
        await store.SealAsync(session, "password1");

        var entry = Assert.Single(await store.GetRecentAsync());
        Assert.True(File.Exists(entry.FilePath));
        Assert.NotEqual("private session data", await File.ReadAllTextAsync(entry.FilePath));

        var reopened = await store.OpenAsync(entry, "password1");
        Assert.Equal("private session data", await File.ReadAllTextAsync(reopened.DatabasePath));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.OpenAsync(entry, "wrongpass"));
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
