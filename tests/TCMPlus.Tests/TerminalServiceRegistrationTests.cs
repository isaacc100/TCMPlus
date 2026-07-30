using Microsoft.Extensions.DependencyInjection;
using TCMPlus.App.TerminalNetworking;
using TCMPlus.App.Updates;
using TCMPlus.App.Views;
using TCMPlus.Domain.Models;
using TCMPlus.Infrastructure.Networking;
using TCMPlus.Protocol;

namespace TCMPlus.Tests;

public sealed class TerminalServiceRegistrationTests
{
    [Fact]
    public void Terminal_services_include_the_updater_required_by_the_main_screen()
    {
        var applicationDataRoot = Path.Combine(
            Path.GetTempPath(),
            "TCMPlus.Tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(applicationDataRoot);

        try
        {
            var host = new Uri("https://127.0.0.1:49321");
            var hostInstanceId = Guid.NewGuid();
            using var remoteService = new RemoteTreatmentCentreService(
                new StubTerminalApiClient(host),
                new EncryptedTerminalCommandQueue(
                    hostInstanceId,
                    host,
                    "Regression terminal",
                    applicationDataRoot));
            var session = new SessionDescriptor(
                Guid.NewGuid(),
                DateTimeOffset.UtcNow,
                "Terminal registration test",
                applicationDataRoot,
                Path.Combine(applicationDataRoot, "session.db"));
            var draft = new TerminalConnectionDraft(
                hostInstanceId,
                host,
                "Regression terminal",
                "temporary-credential",
                new string('A', 64));

            using var services = TerminalServiceProviderFactory.Create(
                session,
                draft,
                remoteService,
                new StubUpdateService());

            Assert.NotNull(services.GetService<IAppUpdateService>());
        }
        finally
        {
            Directory.Delete(applicationDataRoot, recursive: true);
        }
    }

    private sealed class StubTerminalApiClient(Uri host) : ITerminalApiClient
    {
        public Uri Host { get; } = host;
        public TerminalLoginResponse? Login => null;

        public Task<TerminalLoginResponse> AuthenticateAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task DisconnectAsync(CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<TerminalSnapshotResponse> GetSnapshotAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<TerminalCommandResponse> SendCommandAsync(
            TerminalCommandRequest command,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public void Dispose()
        {
        }
    }

    private sealed class StubUpdateService : IAppUpdateService
    {
        public Task<AppUpdateCheckResult> CheckForUpdatesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(AppUpdateCheckResult.UpToDate("0.11.1-DEV"));

        public Task<AppUpdateApplyResult> DownloadAndRestartAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(AppUpdateApplyResult.Unavailable("Not available in tests."));
    }
}
