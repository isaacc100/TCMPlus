using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TCMPlus.App.TerminalNetworking;
using TCMPlus.Domain.Models;
using TCMPlus.Domain.Persistence;
using TCMPlus.Infrastructure.Networking;
using TCMPlus.Infrastructure.Persistence;
using TCMPlus.Infrastructure.Services;
using TCMPlus.Protocol;

namespace TCMPlus.Tests;

public sealed class TerminalNetworkingTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "TCMPlusTerminalTests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Credentials_are_hashed_shift_scoped_and_revocable()
    {
        var fixture = await CreateFixtureAsync();
        var credential = await fixture.Security.CreateAsync("Reception 1", DateTimeOffset.UtcNow.AddHours(8));

        Assert.Matches("^[A-HJ-NP-Z2-9]{4}-[A-HJ-NP-Z2-9]{4}-[A-HJ-NP-Z2-9]{4}$", credential.Password);
        Assert.NotNull(await fixture.Security.VerifyAsync("reception 1", credential.Password, TerminalProtocol.CurrentVersion));
        Assert.Null(await fixture.Security.VerifyAsync("Reception 1", "WRONG-PASSWORD", TerminalProtocol.CurrentVersion));
        Assert.Null(await fixture.Security.VerifyAsync("Reception 1", credential.Password, TerminalProtocol.CurrentVersion + 1));

        await using (var connection = fixture.ConnectionFactory.OpenConnection())
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT password_salt, password_hash FROM terminal_registrations WHERE id = @id;";
            command.Parameters.AddWithValue("@id", credential.Registration.Id.ToString("N"));
            await using var reader = await command.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.NotEqual(credential.Password, reader.GetString(0));
            Assert.NotEqual(credential.Password, reader.GetString(1));
        }

        await fixture.Security.RevokeAsync(credential.Registration.Id);
        Assert.Null(await fixture.Security.VerifyAsync("Reception 1", credential.Password, TerminalProtocol.CurrentVersion));
    }

    [Fact]
    public async Task Concurrent_duplicate_commands_execute_once_and_conflicts_are_audited()
    {
        var fixture = await CreateFixtureAsync();
        var credential = await fixture.Security.CreateAsync("Treatment tent", DateTimeOffset.UtcNow.AddHours(8));
        var station = await fixture.Service.AddStationAsync("Bay 1", "Bed");
        var request = new TerminalCommandRequest(
            Guid.NewGuid(),
            TerminalCommandKind.AddPatientToStation,
            TargetId: station.Id,
            CreatedAt: DateTimeOffset.UtcNow);

        var responses = await Task.WhenAll(
            fixture.Executor.ExecuteAsync(credential.Registration, request),
            fixture.Executor.ExecuteAsync(credential.Registration, request));

        Assert.All(responses, response => Assert.Equal(TerminalCommandStatus.Accepted, response.Status));
        Assert.Equal(responses[0].Sequence, responses[1].Sequence);
        Assert.Single(await fixture.Service.GetPatientsAsync());
        Assert.Single(await fixture.Security.GetAuditAsync());

        var conflicting = await fixture.Executor.ExecuteAsync(
            credential.Registration,
            request with { RequestId = Guid.NewGuid() });
        Assert.Equal(TerminalCommandStatus.Rejected, conflicting.Status);
        Assert.Equal("conflict", conflicting.ErrorCode);
        Assert.Equal(2, (await fixture.Security.GetAuditAsync()).Count);
    }

    [Fact]
    public async Task Snapshot_uses_opaque_patient_references_and_excludes_patient_details()
    {
        var fixture = await CreateFixtureAsync();
        var station = await fixture.Service.AddStationAsync("Bay 2", "Chair");
        var patient = await fixture.Service.AddPatientAsync(station.Id, "Private complaint");

        var snapshot = await fixture.Executor.GetSnapshotAsync();
        var terminalPatient = Assert.Single(snapshot.Stations).Patient;
        Assert.NotNull(terminalPatient);
        Assert.NotEqual(patient.Uid, terminalPatient.Reference);
        var json = JsonSerializer.Serialize(snapshot);
        Assert.DoesNotContain("Private complaint", json, StringComparison.Ordinal);
        Assert.DoesNotContain("presentingComplaint", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(patient.Uid.ToString(), json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Offline_queue_is_encrypted_and_retains_rejections_for_acknowledgement()
    {
        var host = new Uri("https://127.0.0.1:41234");
        var hostInstanceId = Guid.NewGuid();
        var request = new TerminalCommandRequest(
            Guid.NewGuid(),
            TerminalCommandKind.AddMobileTeam,
            Name: "Sensitive callsign",
            CreatedAt: DateTimeOffset.UtcNow);

        using (var queue = new EncryptedTerminalCommandQueue(
                   hostInstanceId,
                   host,
                   "Reception",
                   _root))
        {
            await queue.EnqueueAsync(request);
            await queue.RejectAsync(request.RequestId, 7, "Host state changed.");
            Assert.Equal(1, queue.RejectedCount);
        }

        var queueFile = Assert.Single(Directory.GetFiles(Path.Combine(_root, "TerminalQueues"), "*.tcq"));
        Assert.DoesNotContain("Sensitive callsign", Encoding.UTF8.GetString(await File.ReadAllBytesAsync(queueFile)), StringComparison.Ordinal);
        var keyFile = Assert.Single(Directory.GetFiles(Path.Combine(_root, "TerminalQueueKeys"), "*.key"));
        Assert.DoesNotContain("Sensitive callsign", Encoding.UTF8.GetString(await File.ReadAllBytesAsync(keyFile)), StringComparison.Ordinal);

        using (var reopened = new EncryptedTerminalCommandQueue(
                   hostInstanceId,
                   host,
                   "Reception",
                   _root))
        {
            var rejected = Assert.Single(await reopened.GetAsync());
            Assert.Equal(QueuedTerminalCommandState.Rejected, rejected.State);
            Assert.Equal(7, rejected.HostSequence);
            await reopened.AcknowledgeRejectedAsync();
            Assert.Empty(await reopened.GetAsync());
        }
    }

    [Fact]
    public async Task Terminal_preferences_remember_only_non_secret_connection_hints()
    {
        var store = new TerminalConnectionPreferencesStore(_root);
        await store.SaveAsync(new TerminalConnectionPreferences("Reception 1", "192.168.1.20"));

        var loaded = await store.LoadAsync();
        Assert.Equal("Reception 1", loaded.TerminalName);
        Assert.Equal("192.168.1.20", loaded.HostIdentifier);
        var json = await File.ReadAllTextAsync(Path.Combine(_root, "terminal-connection.json"));
        Assert.DoesNotContain("password", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("fingerprint", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("token", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Terminal_quick_entry_preferences_default_off_and_persist_locally()
    {
        var store = new TerminalOperatorPreferencesStore(_root);

        Assert.False((await store.LoadAsync()).QuickEntry);

        await store.SaveAsync(new TerminalOperatorPreferences(true));
        Assert.True((await store.LoadAsync()).QuickEntry);

        var json = await File.ReadAllTextAsync(Path.Combine(_root, "terminal-operator.json"));
        Assert.Contains("\"quickEntry\": true", json, StringComparison.Ordinal);
        Assert.DoesNotContain("host", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("password", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("token", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Remote_settings_adapter_uses_the_host_snapshot_instead_of_forcing_quick_entry_on()
    {
        var host = new Uri("https://127.0.0.1:42344");
        var api = new FakeTerminalApiClient(host)
        {
            Snapshot = CreateTerminalSnapshot() with { QuickEntry = false }
        };
        using var remote = new RemoteTreatmentCentreService(
            api,
            new EncryptedTerminalCommandQueue(
                Guid.NewGuid(),
                host,
                "Settings test",
                _root));
        var repository = new RemoteTcSettingsRepository(remote);

        Assert.False((await repository.GetAsync()).QuickEntry);

        api.Snapshot = api.Snapshot with { QuickEntry = true };
        await remote.RefreshAsync();
        Assert.True((await repository.GetAsync()).QuickEntry);
    }

    [Fact]
    public async Task Offline_queues_are_bound_to_the_running_host_instance()
    {
        var host = new Uri("https://127.0.0.1:42346");
        var firstHostInstance = Guid.NewGuid();
        var secondHostInstance = Guid.NewGuid();
        var request = new TerminalCommandRequest(
            Guid.NewGuid(),
            TerminalCommandKind.AddMobileTeam,
            Name: "Team 1",
            CreatedAt: DateTimeOffset.UtcNow);

        using (var first = new EncryptedTerminalCommandQueue(
                   firstHostInstance,
                   host,
                   "Reception",
                   _root))
        {
            await first.EnqueueAsync(request);
        }

        using (var reopened = new EncryptedTerminalCommandQueue(
                   firstHostInstance,
                   host,
                   "Reception",
                   _root))
        {
            Assert.Equal(1, reopened.PendingCount);
            Assert.Equal(request.RequestId, Assert.Single(await reopened.GetAsync()).Command.RequestId);
        }

        using var restartedHost = new EncryptedTerminalCommandQueue(
            secondHostInstance,
            host,
            "Reception",
            _root);
        Assert.Empty(await restartedHost.GetAsync());
    }

    [Fact]
    public async Task Unresolved_commands_survive_restart_and_are_never_replayed()
    {
        var host = new Uri("https://127.0.0.1:42347");
        var hostInstance = Guid.NewGuid();
        var station = Guid.NewGuid();
        var request = new TerminalCommandRequest(
            Guid.NewGuid(),
            TerminalCommandKind.AddPatientToStation,
            TargetId: station,
            CreatedAt: DateTimeOffset.UtcNow);

        using (var queue = new EncryptedTerminalCommandQueue(
                   hostInstance,
                   host,
                   "Reception",
                   _root))
        {
            await queue.EnqueueAsync(request);
            await queue.MarkPendingUnresolvedAsync("The previous host session ended.");
            Assert.Equal(0, queue.PendingCount);
            Assert.Equal(1, queue.UnresolvedCount);
        }

        var api = new FakeTerminalApiClient(host)
        {
            Snapshot = CreateTerminalSnapshot(station, Guid.NewGuid()),
            IsOnline = true
        };
        using var remote = new RemoteTreatmentCentreService(
            api,
            new EncryptedTerminalCommandQueue(
                hostInstance,
                host,
                "Reception",
                _root));

        await remote.RefreshAsync();

        Assert.Empty(api.Commands);
        Assert.Equal(1, remote.UnresolvedCommandCount);
        var unresolved = Assert.Single(await remote.GetQueuedCommandsAsync());
        Assert.Equal(QueuedTerminalCommandState.Unresolved, unresolved.State);
        Assert.Equal(request.RequestId, unresolved.Command.RequestId);

        await remote.AcknowledgeUnresolvedCommandsAsync();
        Assert.Empty(await remote.GetQueuedCommandsAsync());
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public async Task Legacy_endpoint_queues_import_as_unresolved_instead_of_replaying()
    {
        var host = new Uri("https://127.0.0.1:42348");
        var request = new TerminalCommandRequest(
            Guid.NewGuid(),
            TerminalCommandKind.AddMobileTeam,
            Name: "Legacy team",
            CreatedAt: DateTimeOffset.UtcNow);
        WriteLegacyQueue(host, "Reception", request);

        using var migrated = new EncryptedTerminalCommandQueue(
            Guid.NewGuid(),
            host,
            "Reception",
            _root);

        Assert.Equal(0, migrated.PendingCount);
        Assert.Equal(1, migrated.UnresolvedCount);
        var imported = Assert.Single(await migrated.GetAsync());
        Assert.Equal(QueuedTerminalCommandState.Unresolved, imported.State);
        Assert.Equal(request.RequestId, imported.Command.RequestId);
        Assert.Contains("could not be verified", imported.RejectionReason);
        Assert.Empty(Directory.GetFiles(
            Path.Combine(_root, "TerminalQueues"),
            "*.v2.tcq"));
        Assert.Single(Directory.GetFiles(
            Path.Combine(_root, "TerminalQueues"),
            "*.v3.tcq"));
    }

    [Theory]
    [InlineData("host_session_closed", HttpStatusCode.Gone, TerminalConnectionState.HostClosed)]
    [InlineData("unauthorized", HttpStatusCode.Unauthorized, TerminalConnectionState.AccessRevoked)]
    [InlineData("protocol_mismatch", HttpStatusCode.UpgradeRequired, TerminalConnectionState.UpdateRequired)]
    public void Terminal_failures_are_classified_without_treating_host_closure_as_a_transient_outage(
        string code,
        HttpStatusCode statusCode,
        TerminalConnectionState expected)
    {
        var result = TerminalConnectionFailureClassifier.Classify(
            new TerminalApiException(code, "Test failure", statusCode));

        Assert.Equal(expected, result.State);
    }

    [Fact]
    public void Network_loss_is_classified_as_reconnecting()
    {
        var result = TerminalConnectionFailureClassifier.Classify(
            new HttpRequestException("Cable disconnected"));

        Assert.Equal(TerminalConnectionState.Reconnecting, result.State);
        Assert.False(result.IsTerminalEnded);
    }

    [Fact]
    public async Task Graceful_host_shutdown_notifies_terminals_before_revoking_access()
    {
        var fixture = await CreateFixtureAsync();
        await using var server = new TerminalHostServer(
            fixture.Security,
            fixture.Executor,
            new TerminalHostServerOptions(
                IPAddress.Loopback,
                ShutdownNotificationDelay: TimeSpan.FromMilliseconds(750)));
        var access = await server.StartAsync();
        var host = new Uri(Assert.Single(access.Addresses));
        var credential = await fixture.Security.CreateAsync(
            "Closing host terminal",
            DateTimeOffset.UtcNow.AddHours(8));
        using var client = new TerminalApiClient(
            host,
            credential.Registration.Name,
            credential.Password,
            access.CertificateFingerprint);
        await client.AuthenticateAsync();

        var stopping = server.StopAsync();
        var deadline = DateTimeOffset.UtcNow.AddSeconds(2);
        while (!server.IsClosing && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Yield();
        }

        Assert.True(server.IsClosing);
        var exception = await Assert.ThrowsAsync<TerminalApiException>(
            () => client.GetSnapshotAsync());
        Assert.Equal(HttpStatusCode.Gone, exception.StatusCode);
        Assert.Equal("host_session_closed", exception.Code);

        await stopping;
        Assert.False(server.IsRunning);
        Assert.All(
            await fixture.Security.GetRegistrationsAsync(),
            registration => Assert.False(registration.IsActive));
    }

    [Fact]
    public async Task Remote_service_replays_offline_commands_and_retains_host_rejections()
    {
        var host = new Uri("https://127.0.0.1:42345");
        var firstStation = Guid.NewGuid();
        var secondStation = Guid.NewGuid();
        var api = new FakeTerminalApiClient(host)
        {
            Snapshot = CreateTerminalSnapshot(firstStation, secondStation),
            IsOnline = false
        };
        using var remote = new RemoteTreatmentCentreService(
            api,
            new EncryptedTerminalCommandQueue(
                Guid.NewGuid(),
                host,
                "Queue test",
                _root));

        await Assert.ThrowsAsync<TerminalCommandQueuedException>(() =>
            remote.AddPatientAsync(firstStation, null));
        Assert.Equal(1, remote.PendingCommandCount);

        api.IsOnline = true;
        api.CommandStatus = TerminalCommandStatus.Accepted;
        await remote.GetSnapshotAsync();
        Assert.Equal(0, remote.PendingCommandCount);
        Assert.Single(api.Commands);

        api.IsOnline = false;
        await Assert.ThrowsAsync<TerminalCommandQueuedException>(() =>
            remote.AddPatientAsync(secondStation, null));
        api.IsOnline = true;
        api.CommandStatus = TerminalCommandStatus.Rejected;
        await remote.RefreshAsync();

        Assert.Equal(1, remote.RejectedCommandCount);
        var rejected = Assert.Single(await remote.GetQueuedCommandsAsync());
        Assert.Equal(QueuedTerminalCommandState.Rejected, rejected.State);
        Assert.Contains("Host rejected stale state", rejected.RejectionReason);
    }

    [Fact]
    public async Task Https_host_requires_pinned_certificate_authentication_and_protocol()
    {
        var fixture = await CreateFixtureAsync();
        var station = await fixture.Service.AddStationAsync("Bay 3", "Bed");
        await using var server = new TerminalHostServer(
            fixture.Security,
            fixture.Executor,
            new TerminalHostServerOptions(IPAddress.Loopback));
        var access = await server.StartAsync();
        var host = new Uri(Assert.Single(access.Addresses));
        var credential = await fixture.Security.CreateAsync("Remote app", DateTimeOffset.UtcNow.AddHours(8));

        using (var rawClient = new HttpClient(new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, _, _, _) => true
        }) { BaseAddress = host })
        {
            var mismatch = await rawClient.PostAsJsonAsync(
                $"{TerminalProtocol.ApiRoot}/auth/token",
                new TerminalLoginRequest("Unsupported client", "wrong", TerminalProtocol.CurrentVersion + 1));
            Assert.Equal(HttpStatusCode.UpgradeRequired, mismatch.StatusCode);

            using var unauthorizedSnapshot = new HttpRequestMessage(HttpMethod.Get, $"{TerminalProtocol.ApiRoot}/snapshot");
            unauthorizedSnapshot.Headers.Add(TerminalProtocol.VersionHeader, TerminalProtocol.CurrentVersion.ToString());
            Assert.Equal(HttpStatusCode.Unauthorized, (await rawClient.SendAsync(unauthorizedSnapshot)).StatusCode);

            for (var attempt = 0; attempt < 5; attempt++)
            {
                var failed = await rawClient.PostAsJsonAsync(
                    $"{TerminalProtocol.ApiRoot}/auth/token",
                    new TerminalLoginRequest("Unknown terminal", "wrong", TerminalProtocol.CurrentVersion));
                Assert.Equal(HttpStatusCode.Unauthorized, failed.StatusCode);
            }

            var limited = await rawClient.PostAsJsonAsync(
                $"{TerminalProtocol.ApiRoot}/auth/token",
                new TerminalLoginRequest("Unknown terminal", "wrong", TerminalProtocol.CurrentVersion));
            Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await rawClient.GetAsync($"{TerminalProtocol.ApiRoot}/admin")).StatusCode);
        }

        using var client = new TerminalApiClient(host, credential.Registration.Name, credential.Password, access.CertificateFingerprint);
        var login = await client.AuthenticateAsync();
        Assert.Equal(credential.Registration.Id, login.TerminalId);
        Assert.Single((await client.GetSnapshotAsync()).Stations);

        var command = new TerminalCommandRequest(Guid.NewGuid(), TerminalCommandKind.AddPatientToStation, TargetId: station.Id);
        Assert.Equal(TerminalCommandStatus.Accepted, (await client.SendCommandAsync(command)).Status);

        using var wrongFingerprintClient = new TerminalApiClient(host, credential.Registration.Name, credential.Password, new string('A', 64));
        await Assert.ThrowsAsync<HttpRequestException>(() => wrongFingerprintClient.AuthenticateAsync());

        await server.RevokeTerminalAsync(credential.Registration.Id);
        var exception = await Assert.ThrowsAsync<TerminalApiException>(() => client.GetSnapshotAsync());
        Assert.Equal("invalid_credentials", exception.Code);
    }

    [Fact]
    public async Task Pairing_requires_host_code_approval_and_returns_a_pinned_in_memory_credential()
    {
        var fixture = await CreateFixtureAsync();
        var station = await fixture.Service.AddStationAsync("Pairing bay", "Bed");
        await using var server = new TerminalHostServer(
            fixture.Security,
            fixture.Executor,
            new TerminalHostServerOptions(IPAddress.Loopback));
        var access = await server.StartAsync();
        var host = new Uri(Assert.Single(access.Addresses));

        using var pairing = await TerminalPairingClient.StartAsync(host, "Reception tablet", "0.11.0-DEV");
        var request = Assert.Single(server.GetPendingPairings());
        Assert.Equal("Reception tablet", request.TerminalName);
        Assert.Matches("^[0-9]{6}$", pairing.VerificationCode);

        var approved = await server.ApprovePairingAsync(request.PairingId, pairing.VerificationCode);
        Assert.True(approved.Approved);
        var result = await pairing.WaitForApprovalAsync();
        Assert.Equal(host, result.Host);
        Assert.Equal(access.HostInstanceId, result.HostInstanceId);
        Assert.Equal("Reception tablet", result.TerminalName);
        Assert.Equal(TerminalProtocol.CurrentVersion, result.ProtocolVersion);

        using var api = new TerminalApiClient(
            result.Host,
            result.TerminalName,
            result.Password,
            result.CertificateFingerprint);
        var login = await api.AuthenticateAsync();
        Assert.Equal(result.TerminalId, login.TerminalId);
        Assert.Equal(station.Id, Assert.Single((await api.GetSnapshotAsync()).Stations).Id);

        var audit = await fixture.Security.GetPairingAuditAsync();
        Assert.Equal(["Approved", "Created"], audit.Select(entry => entry.Result).ToArray());
        var serializedAudit = JsonSerializer.Serialize(audit);
        Assert.DoesNotContain(pairing.VerificationCode, serializedAudit, StringComparison.Ordinal);
        Assert.DoesNotContain(result.Password, serializedAudit, StringComparison.Ordinal);
        Assert.DoesNotContain(result.CertificateFingerprint, serializedAudit, StringComparison.Ordinal);

        await api.DisconnectAsync();
        Assert.Null(await fixture.Security.VerifyAsync(
            result.TerminalName,
            result.Password,
            TerminalProtocol.CurrentVersion));
    }

    [Fact]
    public async Task Incorrect_pairing_code_is_single_attempt_and_replay_safe()
    {
        var fixture = await CreateFixtureAsync();
        await using var server = new TerminalHostServer(
            fixture.Security,
            fixture.Executor,
            new TerminalHostServerOptions(IPAddress.Loopback));
        var access = await server.StartAsync();
        using var pairing = await TerminalPairingClient.StartAsync(
            new Uri(Assert.Single(access.Addresses)),
            "Wrong code terminal",
            "0.11.0-DEV");
        var request = Assert.Single(server.GetPendingPairings());
        var incorrect = pairing.VerificationCode == "000000" ? "000001" : "000000";

        var rejected = await server.ApprovePairingAsync(request.PairingId, incorrect);
        Assert.False(rejected.Approved);
        var exception = await Assert.ThrowsAsync<TerminalPairingException>(() => pairing.WaitForApprovalAsync());
        Assert.Equal("verification_failed", exception.Code);

        var replay = await server.ApprovePairingAsync(request.PairingId, pairing.VerificationCode);
        Assert.False(replay.Approved);
        Assert.DoesNotContain(
            await fixture.Security.GetRegistrationsAsync(),
            registration => registration.IsActive);
        Assert.Equal(
            "Rejected",
            Assert.Single(
                await fixture.Security.GetPairingAuditAsync(),
                entry => entry.Result != "Created").Result);
    }

    [Fact]
    public async Task Pairing_expires_and_never_exposes_a_bootstrap_secret_before_approval()
    {
        var fixture = await CreateFixtureAsync();
        await using var server = new TerminalHostServer(
            fixture.Security,
            fixture.Executor,
            new TerminalHostServerOptions(
                IPAddress.Loopback,
                PairingLifetime: TimeSpan.FromMilliseconds(150)));
        var access = await server.StartAsync();
        var host = new Uri(Assert.Single(access.Addresses));
        using var key = new TerminalPairingKeyExchange();
        var request = new TerminalPairingStartRequest(
            Guid.NewGuid(),
            "Expiring terminal",
            "0.11.0-DEV",
            TerminalProtocol.CurrentVersion,
            key.PublicKey,
            key.Nonce);
        using var rawClient = new HttpClient(new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, _, _, _) => true
        }) { BaseAddress = host };

        using var startResponse = await rawClient.PostAsJsonAsync(
            $"{TerminalProtocol.PairingApiRoot}/start",
            request);
        var startJson = await startResponse.Content.ReadAsStringAsync();
        Assert.DoesNotContain("password", startJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("verificationCode", startJson, StringComparison.OrdinalIgnoreCase);
        var started = JsonSerializer.Deserialize<TerminalPairingStartResponse>(
            startJson,
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

        using var pendingResponse = await rawClient.GetAsync(
            $"{TerminalProtocol.PairingApiRoot}/{started.PairingId:N}");
        var pendingJson = await pendingResponse.Content.ReadAsStringAsync();
        Assert.Contains("\"status\":\"Pending\"", pendingJson, StringComparison.Ordinal);
        Assert.DoesNotContain("encryptedCredential\":\"", pendingJson, StringComparison.Ordinal);

        await Task.Delay(250);
        using var expiredResponse = await rawClient.GetAsync(
            $"{TerminalProtocol.PairingApiRoot}/{started.PairingId:N}");
        var expired = await expiredResponse.Content.ReadFromJsonAsync<TerminalPairingStatusResponse>();
        Assert.Equal(TerminalPairingStatus.Expired, expired!.Status);
    }

    [Fact]
    public async Task Discovery_supports_address_only_lookup_without_exposing_credentials()
    {
        var portProbe = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var discoveryPort = ((IPEndPoint)portProbe.Client.LocalEndPoint!).Port;
        portProbe.Dispose();
        var options = new TerminalDiscoveryOptions(
            discoveryPort,
            IPAddress.Parse(TerminalProtocol.DiscoveryMulticastAddress),
            JoinMulticast: false,
            SendBroadcast: false);
        var advertisement = new TerminalDiscoveryAdvertisement(
            TerminalProtocol.DiscoveryMagic,
            TerminalProtocol.CurrentVersion,
            Guid.NewGuid(),
            "A7K9",
            41234,
            "0.11.0-DEV");
        await using var responder = new TerminalDiscoveryResponder(advertisement, options);
        await responder.StartAsync();

        var host = Assert.Single(await TerminalDiscoveryClient.ResolveAsync(
            "127.0.0.1",
            TimeSpan.FromMilliseconds(500),
            options));
        Assert.Equal("A7K9", host.HostCode);
        Assert.Equal(new Uri("https://127.0.0.1:41234"), host.Host);
        var json = JsonSerializer.Serialize(advertisement);
        Assert.DoesNotContain("password", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("fingerprint", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("patient", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Pairing_crypto_detects_tampering_and_key_substitution()
    {
        using var clientKey = new TerminalPairingKeyExchange();
        using var hostKey = new TerminalPairingKeyExchange();
        var request = new TerminalPairingStartRequest(
            Guid.NewGuid(),
            "Crypto terminal",
            "0.11.0-DEV",
            TerminalProtocol.CurrentVersion,
            clientKey.PublicKey,
            clientKey.Nonce);
        var response = new TerminalPairingStartResponse(
            Guid.NewGuid(),
            Guid.NewGuid(),
            hostKey.PublicKey,
            hostKey.Nonce,
            new string('A', 64),
            DateTimeOffset.UtcNow.AddMinutes(2),
            TerminalProtocol.CurrentVersion);
        using var clientSecrets = clientKey.DeriveAsClient(request, response);
        using var hostSecrets = hostKey.DeriveAsHost(request, response);
        Assert.Equal(clientSecrets.VerificationCode, hostSecrets.VerificationCode);

        var encrypted = hostSecrets.Encrypt(new TerminalPairingBootstrapCredential(
            Guid.NewGuid(),
            response.HostInstanceId,
            "Crypto terminal",
            "not-human-readable",
            new string('A', 64),
            TerminalProtocol.CurrentVersion));
        var tamperedBytes = Convert.FromBase64String(encrypted.Ciphertext);
        tamperedBytes[0] ^= 0x40;
        Assert.Throws<InvalidOperationException>(() => clientSecrets.Decrypt(
            encrypted with { Ciphertext = Convert.ToBase64String(tamperedBytes) }));

        using var attackerKey = new TerminalPairingKeyExchange();
        using var substituted = clientKey.DeriveAsClient(
            request,
            response with { HostPublicKey = attackerKey.PublicKey });
        Assert.Throws<InvalidOperationException>(() => substituted.Decrypt(encrypted));

        using var substitutedHostIdentity = clientKey.DeriveAsClient(
            request,
            response with { HostInstanceId = Guid.NewGuid() });
        Assert.Throws<InvalidOperationException>(() => substitutedHostIdentity.Decrypt(encrypted));
    }

    [Fact]
    public async Task Pairing_start_is_rate_limited_per_source()
    {
        var fixture = await CreateFixtureAsync();
        await using var server = new TerminalHostServer(
            fixture.Security,
            fixture.Executor,
            new TerminalHostServerOptions(IPAddress.Loopback));
        var access = await server.StartAsync();
        var host = new Uri(Assert.Single(access.Addresses));
        for (var index = 0; index < 3; index++)
        {
            using var pairing = await TerminalPairingClient.StartAsync(
                host,
                $"Rate terminal {index}",
                "0.11.0-DEV");
        }

        var exception = await Assert.ThrowsAsync<TerminalPairingException>(() =>
            TerminalPairingClient.StartAsync(host, "Rate terminal blocked", "0.11.0-DEV"));
        Assert.Equal("rate_limited", exception.Code);
        Assert.Equal(HttpStatusCode.TooManyRequests, exception.StatusCode);
    }

    [Fact]
    public async Task Legacy_v1_operational_clients_remain_compatible()
    {
        var fixture = await CreateFixtureAsync();
        await fixture.Service.AddStationAsync("Legacy bay", "Bed");
        await using var server = new TerminalHostServer(
            fixture.Security,
            fixture.Executor,
            new TerminalHostServerOptions(IPAddress.Loopback));
        var access = await server.StartAsync();
        var credential = await fixture.Security.CreateAsync(
            "Legacy terminal",
            DateTimeOffset.UtcNow.AddHours(8),
            TerminalProtocol.LegacyVersion);
        using var client = new HttpClient(new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, _, _, _) => true
        }) { BaseAddress = new Uri(Assert.Single(access.Addresses)) };

        using var loginResponse = await client.PostAsJsonAsync(
            $"{TerminalProtocol.ApiRoot}/auth/token",
            new TerminalLoginRequest(
                credential.Registration.Name,
                credential.Password,
                TerminalProtocol.LegacyVersion));
        Assert.Equal(HttpStatusCode.OK, loginResponse.StatusCode);
        var login = await loginResponse.Content.ReadFromJsonAsync<TerminalLoginResponse>();
        Assert.Equal(TerminalProtocol.LegacyVersion, login!.ProtocolVersion);

        using var snapshotRequest = new HttpRequestMessage(HttpMethod.Get, $"{TerminalProtocol.ApiRoot}/snapshot");
        snapshotRequest.Headers.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", login.AccessToken);
        snapshotRequest.Headers.Add(TerminalProtocol.VersionHeader, TerminalProtocol.LegacyVersion.ToString());
        using var snapshotResponse = await client.SendAsync(snapshotRequest);
        Assert.Equal(HttpStatusCode.OK, snapshotResponse.StatusCode);
        Assert.Single((await snapshotResponse.Content.ReadFromJsonAsync<TerminalSnapshotResponse>())!.Stations);
    }

    [Fact]
    public async Task Host_shutdown_revokes_every_temporary_terminal()
    {
        var fixture = await CreateFixtureAsync();
        var stale = await fixture.Security.CreateAsync("Stale", DateTimeOffset.UtcNow.AddHours(8));
        await using var server = new TerminalHostServer(
            fixture.Security,
            fixture.Executor,
            new TerminalHostServerOptions(IPAddress.Loopback));
        await server.StartAsync();
        Assert.Null(await fixture.Security.VerifyAsync(
            stale.Registration.Name,
            stale.Password,
            TerminalProtocol.CurrentVersion));
        var first = await fixture.Security.CreateAsync("First", DateTimeOffset.UtcNow.AddHours(8));
        var second = await fixture.Security.CreateAsync("Second", DateTimeOffset.UtcNow.AddHours(8));

        await server.StopAsync();

        Assert.Null(await fixture.Security.VerifyAsync(first.Registration.Name, first.Password, TerminalProtocol.CurrentVersion));
        Assert.Null(await fixture.Security.VerifyAsync(second.Registration.Name, second.Password, TerminalProtocol.CurrentVersion));
    }

    private async Task<Fixture> CreateFixtureAsync()
    {
        Directory.CreateDirectory(_root);
        var databasePath = Path.Combine(_root, $"{Guid.NewGuid():N}.sqlite");
        var connectionFactory = new SqliteConnectionFactory(databasePath);
        await new DatabaseInitializer(connectionFactory).InitializeAsync();
        var settings = new SqliteTcSettingsRepository(connectionFactory);
        await settings.SaveAsync(new TcSessionSettings("Network test shift", null, null, true, GridDensity.Standard));
        var local = new TreatmentCentreService(
            new SqliteStationRepository(connectionFactory),
            new SqlitePatientRepository(connectionFactory),
            new SqliteMobileTeamRepository(connectionFactory));
        var serialized = new SerializedTreatmentCentreService(local);
        var security = new TerminalSecurityStore(connectionFactory);
        var executor = new TerminalCommandExecutor(serialized, settings, new FakeAppSettingsRepository(), security);
        return new Fixture(connectionFactory, serialized, security, executor);
    }

    private static TerminalSnapshotResponse CreateTerminalSnapshot(params Guid[] stationIds) => new(
        0,
        DateTimeOffset.UtcNow,
        "Remote test",
        TerminalGridDensity.Standard,
        true,
        AppSettings.Default.DischargeRoutes,
        stationIds.Select((id, index) => new TerminalStation(id, $"Bay {index + 1}", "Bed", index * 8, 0, 7, 7, null)).ToList(),
        [],
        new TerminalDashboard(stationIds.Length, 0, 0, null, [], []));

    [SupportedOSPlatform("windows")]
    private void WriteLegacyQueue(
        Uri host,
        string terminalName,
        TerminalCommandRequest request)
    {
        var identity = SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{host.GetLeftPart(UriPartial.Authority)}|{terminalName.Trim().ToUpperInvariant()}"));
        var identityText = Convert.ToHexString(identity)[..24];
        var key = RandomNumberGenerator.GetBytes(32);
        try
        {
            var keyDirectory = Path.Combine(_root, "TerminalQueueKeys");
            Directory.CreateDirectory(keyDirectory);
            var protectedKey = ProtectedData.Protect(
                key,
                identity,
                DataProtectionScope.CurrentUser);
            File.WriteAllBytes(
                Path.Combine(keyDirectory, $"{identityText}.key"),
                "TQK1"u8.ToArray().Concat(protectedKey).ToArray());

            var plaintext = JsonSerializer.SerializeToUtf8Bytes(
                new[]
                {
                    new QueuedTerminalCommand(
                        request,
                        QueuedTerminalCommandState.Pending)
                },
                new JsonSerializerOptions(JsonSerializerDefaults.Web));
            try
            {
                var magic = "TCQ2"u8.ToArray();
                var nonce = RandomNumberGenerator.GetBytes(12);
                var ciphertext = new byte[plaintext.Length];
                var tag = new byte[16];
                using (var aes = new AesGcm(key, tag.Length))
                {
                    aes.Encrypt(nonce, plaintext, ciphertext, tag, magic);
                }

                var queueDirectory = Path.Combine(_root, "TerminalQueues");
                Directory.CreateDirectory(queueDirectory);
                File.WriteAllBytes(
                    Path.Combine(queueDirectory, $"{identityText}.v2.tcq"),
                    magic.Concat(nonce).Concat(tag).Concat(ciphertext).ToArray());
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plaintext);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, true);
        }
    }

    private sealed record Fixture(
        SqliteConnectionFactory ConnectionFactory,
        SerializedTreatmentCentreService Service,
        TerminalSecurityStore Security,
        TerminalCommandExecutor Executor);

    private sealed class FakeAppSettingsRepository : IAppSettingsRepository
    {
        public Task<AppSettings> GetAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(AppSettings.Default);

        public Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class FakeTerminalApiClient(Uri host) : ITerminalApiClient
    {
        public Uri Host { get; } = host;
        public TerminalLoginResponse? Login { get; private set; }
        public bool IsOnline { get; set; } = true;
        public TerminalCommandStatus CommandStatus { get; set; } = TerminalCommandStatus.Accepted;
        public TerminalSnapshotResponse Snapshot { get; set; } = CreateTerminalSnapshot();
        public List<TerminalCommandRequest> Commands { get; } = [];

        public Task<TerminalLoginResponse> AuthenticateAsync(CancellationToken cancellationToken = default)
        {
            ThrowIfOffline();
            Login = new TerminalLoginResponse("token", DateTimeOffset.UtcNow.AddMinutes(15), Guid.NewGuid(), "Fake", Snapshot.ShiftName, TerminalProtocol.CurrentVersion);
            return Task.FromResult(Login);
        }

        public Task DisconnectAsync(CancellationToken cancellationToken = default)
        {
            Login = null;
            return Task.CompletedTask;
        }

        public Task<TerminalSnapshotResponse> GetSnapshotAsync(CancellationToken cancellationToken = default)
        {
            ThrowIfOffline();
            return Task.FromResult(Snapshot);
        }

        public Task<TerminalCommandResponse> SendCommandAsync(TerminalCommandRequest command, CancellationToken cancellationToken = default)
        {
            ThrowIfOffline();
            Commands.Add(command);
            return Task.FromResult(new TerminalCommandResponse(
                command.RequestId,
                CommandStatus,
                Commands.Count,
                DateTimeOffset.UtcNow,
                CommandStatus == TerminalCommandStatus.Rejected ? "conflict" : null,
                CommandStatus == TerminalCommandStatus.Rejected ? "Host rejected stale state." : null));
        }

        public void Dispose()
        {
        }

        private void ThrowIfOffline()
        {
            if (!IsOnline)
            {
                throw new HttpRequestException("Offline");
            }
        }
    }
}
