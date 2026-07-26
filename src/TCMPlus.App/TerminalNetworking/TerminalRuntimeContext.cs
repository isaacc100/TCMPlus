using TCMPlus.Infrastructure.Networking;

namespace TCMPlus.App.TerminalNetworking;

public sealed record TerminalRuntimeContext(
    bool IsTerminal,
    TerminalHostServer? HostServer = null,
    RemoteTreatmentCentreService? RemoteService = null,
    string? TerminalName = null,
    string? HostAddress = null)
{
    public static TerminalRuntimeContext Host(TerminalHostServer server) => new(false, HostServer: server);

    public static TerminalRuntimeContext Terminal(
        RemoteTreatmentCentreService service,
        string terminalName,
        string hostAddress) =>
        new(true, RemoteService: service, TerminalName: terminalName, HostAddress: hostAddress);
}
