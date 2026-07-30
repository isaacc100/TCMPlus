using System.Runtime.InteropServices;
using TCMPlus.App.Updates;

namespace TCMPlus.Tests;

public sealed class AppUpdateChannelTests
{
    [Theory]
    [InlineData("win", Architecture.X64, false, "win-x64-stable")]
    [InlineData("win", Architecture.X64, true, "win-x64-dev")]
    [InlineData("osx", Architecture.Arm64, false, "osx-arm64-stable")]
    [InlineData("linux", Architecture.Arm64, true, "linux-arm64-dev")]
    public void Builds_platform_architecture_and_release_channel(string platform, Architecture architecture, bool developmentBuild, string expected)
    {
        Assert.Equal(expected, AppUpdateChannel.For(platform, architecture, developmentBuild));
    }

    [Fact]
    public void Rejects_unsupported_architecture()
    {
        Assert.Throws<PlatformNotSupportedException>(() => AppUpdateChannel.For("win", Architecture.X86, false));
    }

    [Fact]
    public void Update_result_models_keep_unavailable_checks_non_fatal()
    {
        var upToDate = AppUpdateCheckResult.UpToDate("0.11.0-DEV");
        var available = AppUpdateCheckResult.Available("0.12.0-DEV", "- A newer release.");
        var unavailable = AppUpdateCheckResult.Unavailable("Unable to check for updates. Try again later.");
        var failedApply = AppUpdateApplyResult.Failed("Unable to download or install the update. Try again later.");

        Assert.Equal(AppUpdateStatus.UpToDate, upToDate.Status);
        Assert.Equal(AppUpdateStatus.Available, available.Status);
        Assert.Equal("0.12.0-DEV", available.Version);
        Assert.Equal(AppUpdateStatus.Unavailable, unavailable.Status);
        Assert.False(failedApply.Started);
    }
}
