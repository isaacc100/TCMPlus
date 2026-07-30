using System.Reflection;
using System.Runtime.InteropServices;

namespace TCMPlus.App.Updates;

public static class AppUpdateChannel
{
    public static bool IsDevelopmentBuild(Assembly? assembly = null)
    {
        var informationalVersion = (assembly ?? typeof(AppUpdateChannel).Assembly)
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        return informationalVersion?.EndsWith("-DEV", StringComparison.OrdinalIgnoreCase) == true;
    }

    public static string Current => For(GetPlatform(), RuntimeInformation.ProcessArchitecture, IsDevelopmentBuild());

    public static string For(string platform, Architecture architecture, bool developmentBuild)
    {
        var architectureName = architecture switch
        {
            Architecture.X64 => "x64",
            Architecture.Arm64 => "arm64",
            _ => throw new PlatformNotSupportedException($"TCM+ updates do not support the {architecture} architecture.")
        };

        return $"{platform}-{architectureName}-{(developmentBuild ? "dev" : "stable")}";
    }

    private static string GetPlatform()
    {
        if (OperatingSystem.IsWindows()) return "win";
        if (OperatingSystem.IsMacOS()) return "osx";
        if (OperatingSystem.IsLinux()) return "linux";
        throw new PlatformNotSupportedException("TCM+ updates are not supported on this operating system.");
    }
}
