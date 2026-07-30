using System.Net;
using TCMPlus.Infrastructure.Networking;

namespace TCMPlus.App.TerminalNetworking;

public enum TerminalConnectionState
{
    Connected,
    Reconnecting,
    HostClosed,
    AccessRevoked,
    UpdateRequired
}

public sealed record TerminalConnectionFailure(
    TerminalConnectionState State,
    string Message)
{
    public bool IsTerminalEnded => State is
        TerminalConnectionState.HostClosed
        or TerminalConnectionState.AccessRevoked
        or TerminalConnectionState.UpdateRequired;
}

public static class TerminalConnectionFailureClassifier
{
    public static TerminalConnectionFailure Classify(
        Exception exception,
        CancellationToken cancellationToken = default)
    {
        var relevant = Unwrap(exception);
        if (relevant is TerminalApiException apiException)
        {
            if (apiException.StatusCode == HttpStatusCode.Gone
                || string.Equals(
                    apiException.Code,
                    "host_session_closed",
                    StringComparison.OrdinalIgnoreCase))
            {
                return new TerminalConnectionFailure(
                    TerminalConnectionState.HostClosed,
                    "The authoritative host closed this terminal session.");
            }

            if (apiException.StatusCode == HttpStatusCode.Unauthorized
                || apiException.Code is "invalid_credentials" or "unauthorized")
            {
                return new TerminalConnectionFailure(
                    TerminalConnectionState.AccessRevoked,
                    "This terminal's access was revoked or expired.");
            }

            if (apiException.StatusCode == HttpStatusCode.UpgradeRequired
                || string.Equals(
                    apiException.Code,
                    "protocol_mismatch",
                    StringComparison.OrdinalIgnoreCase))
            {
                return new TerminalConnectionFailure(
                    TerminalConnectionState.UpdateRequired,
                    "This terminal must be updated before it can reconnect.");
            }
        }

        if (relevant is HttpRequestException
            || relevant is TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return new TerminalConnectionFailure(
                TerminalConnectionState.Reconnecting,
                "The LAN connection to the host was interrupted.");
        }

        return new TerminalConnectionFailure(
            TerminalConnectionState.Reconnecting,
            $"The host is temporarily unavailable: {relevant.Message}");
    }

    private static Exception Unwrap(Exception exception)
    {
        while (exception is AggregateException { InnerExceptions.Count: 1 } aggregate)
        {
            exception = aggregate.InnerExceptions[0];
        }

        return exception is TerminalCommandQueuedException { InnerException: { } inner }
            ? Unwrap(inner)
            : exception;
    }
}
