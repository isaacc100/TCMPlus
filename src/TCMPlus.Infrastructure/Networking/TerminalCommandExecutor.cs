using System.Security.Cryptography;
using System.Text;
using TCMPlus.Domain.Models;
using TCMPlus.Domain.Persistence;
using TCMPlus.Protocol;

namespace TCMPlus.Infrastructure.Networking;

public sealed class TerminalCommandExecutor(
    SerializedTreatmentCentreService treatmentCentre,
    ITcSettingsRepository settingsRepository,
    IAppSettingsRepository appSettingsRepository,
    TerminalSecurityStore securityStore)
{
    private readonly byte[] _patientReferenceKey = RandomNumberGenerator.GetBytes(32);
    private readonly SemaphoreSlim _commandGate = new(1, 1);

    public async Task<TerminalSnapshotResponse> GetSnapshotAsync(CancellationToken cancellationToken = default)
    {
        var stationsTask = treatmentCentre.GetSnapshotAsync(cancellationToken);
        var teamsTask = treatmentCentre.GetMobileTeamsAsync(cancellationToken);
        var dashboardTask = treatmentCentre.GetDashboardAsync(cancellationToken);
        var settingsTask = settingsRepository.GetAsync(cancellationToken);
        var appSettingsTask = appSettingsRepository.GetAsync(cancellationToken);
        var sequenceTask = securityStore.GetCurrentSequenceAsync(cancellationToken);
        await Task.WhenAll(stationsTask, teamsTask, dashboardTask, settingsTask, appSettingsTask, sequenceTask);

        var settings = await settingsTask;
        var appSettings = await appSettingsTask;
        var dashboard = await dashboardTask;
        return new TerminalSnapshotResponse(
            await sequenceTask,
            DateTimeOffset.UtcNow,
            settings.ShiftName ?? "TCM+ shift",
            (TerminalGridDensity)(int)settings.GridDensity,
            settings.QuickEntry,
            appSettings.DischargeRoutes,
            (await stationsTask).Select(item => new TerminalStation(
                item.Station.Id,
                item.Station.Name,
                item.Station.Type,
                item.Station.GridX,
                item.Station.GridY,
                item.Station.GridWidth,
                item.Station.GridHeight,
                ToTerminalPatient(item.CurrentPatient))).ToList(),
            (await teamsTask).Select(item => new TerminalMobileTeam(
                item.Team.Id,
                item.Team.Callsign,
                item.Team.Note,
                item.Team.IsDeployed,
                item.Team.DeploymentLocation,
                ToTerminalPatient(item.CurrentPatient))).ToList(),
            new TerminalDashboard(
                dashboard.AvailableStations,
                dashboard.OccupiedStations,
                dashboard.PatientsSeen,
                dashboard.AverageDischargeDuration?.Ticks,
                dashboard.Occupancy.Select(point => new TerminalChartPoint(point.ObservedAt, point.OccupiedStations)).ToList(),
                dashboard.CumulativeArrivals.Select(point => new TerminalChartPoint(point.ObservedAt, point.PatientsSeen)).ToList()));
    }

    public async Task<TerminalCommandResponse> ExecuteAsync(
        TerminalRegistration terminal,
        TerminalCommandRequest request,
        CancellationToken cancellationToken = default)
    {
        await _commandGate.WaitAsync(cancellationToken);
        try
        {
            return await ExecuteCoreAsync(terminal, request, cancellationToken);
        }
        finally
        {
            _commandGate.Release();
        }
    }

    private async Task<TerminalCommandResponse> ExecuteCoreAsync(
        TerminalRegistration terminal,
        TerminalCommandRequest request,
        CancellationToken cancellationToken)
    {
        if (request.RequestId == Guid.Empty)
        {
            return new TerminalCommandResponse(Guid.Empty, TerminalCommandStatus.Rejected, 0, DateTimeOffset.UtcNow, "invalid_request", "A non-empty request ID is required.");
        }

        var pending = await securityStore.BeginCommandAsync(terminal, request, AuditTarget(request), cancellationToken);
        if (pending.ExistingResponse is not null)
        {
            return pending.ExistingResponse;
        }

        TerminalCommandResponse response;
        try
        {
            ValidateRequest(request);
            await ExecuteOperationAsync(request, cancellationToken);
            response = new TerminalCommandResponse(request.RequestId, TerminalCommandStatus.Accepted, pending.Sequence, DateTimeOffset.UtcNow);
        }
        catch (InvalidOperationException exception)
        {
            response = new TerminalCommandResponse(request.RequestId, TerminalCommandStatus.Rejected, pending.Sequence, DateTimeOffset.UtcNow, "conflict", exception.Message);
        }
        catch (ArgumentException exception)
        {
            response = new TerminalCommandResponse(request.RequestId, TerminalCommandStatus.Rejected, pending.Sequence, DateTimeOffset.UtcNow, "invalid_request", exception.Message);
        }
        catch
        {
            response = new TerminalCommandResponse(request.RequestId, TerminalCommandStatus.Rejected, pending.Sequence, DateTimeOffset.UtcNow, "server_error", "The host could not complete this command.");
        }

        await securityStore.CompleteCommandAsync(response, cancellationToken);
        return response;
    }

    private async Task ExecuteOperationAsync(TerminalCommandRequest request, CancellationToken cancellationToken)
    {
        switch (request.Kind)
        {
            case TerminalCommandKind.AddPatientToStation:
                await treatmentCentre.ExecuteRemoteAsync(
                    service => service.AddPatientAsync(Required(request.TargetId, "station"), null, cancellationToken),
                    cancellationToken);
                break;

            case TerminalCommandKind.AddPatientToMobileTeam:
                await treatmentCentre.ExecuteRemoteAsync(
                    service => service.AddPatientToMobileTeamAsync(Required(request.TargetId, "mobile team"), null, cancellationToken),
                    cancellationToken);
                break;

            case TerminalCommandKind.MovePatient:
            {
                var patientUid = await ResolvePatientReferenceAsync(Required(request.TargetId, "patient"), cancellationToken);
                var destination = new PatientAssignment(
                    request.DestinationKind == TerminalAssignmentKind.MobileTeam ? PatientAssignmentKind.MobileTeam : PatientAssignmentKind.Station,
                    Required(request.SecondaryId, "destination"));
                await treatmentCentre.ExecuteRemoteAsync(
                    service => service.MovePatientAsync(patientUid, destination, request.Swap, cancellationToken),
                    cancellationToken);
                break;
            }

            case TerminalCommandKind.DischargePatient:
            {
                var patientUid = await ResolvePatientReferenceAsync(Required(request.TargetId, "patient"), cancellationToken);
                await treatmentCentre.ExecuteRemoteAsync(
                    service => service.DischargeAssignedPatientAsync(patientUid, request.DischargeRoute, request.DischargeOutcome, cancellationToken),
                    cancellationToken);
                break;
            }

            case TerminalCommandKind.AddMobileTeam:
                await treatmentCentre.ExecuteRemoteAsync(
                    service => service.AddMobileTeamAsync(request.Name ?? string.Empty, request.Note, cancellationToken),
                    cancellationToken);
                break;

            case TerminalCommandKind.UpdateMobileTeam:
                await treatmentCentre.ExecuteRemoteAsync(
                    service => service.UpdateMobileTeamAsync(Required(request.TargetId, "mobile team"), request.Name ?? string.Empty, request.Note, cancellationToken),
                    cancellationToken);
                break;

            case TerminalCommandKind.DeployMobileTeam:
                await treatmentCentre.ExecuteRemoteAsync(
                    service => service.DeployMobileTeamAsync(Required(request.TargetId, "mobile team"), request.Location, cancellationToken),
                    cancellationToken);
                break;

            case TerminalCommandKind.UpdateMobileTeamLocation:
                await treatmentCentre.ExecuteRemoteAsync(
                    service => service.UpdateMobileTeamLocationAsync(Required(request.TargetId, "mobile team"), request.Location, cancellationToken),
                    cancellationToken);
                break;

            case TerminalCommandKind.StandDownMobileTeam:
                await treatmentCentre.ExecuteRemoteAsync(
                    service => service.StandDownMobileTeamAsync(Required(request.TargetId, "mobile team"), cancellationToken),
                    cancellationToken);
                break;

            default:
                throw new ArgumentException("This command is not supported by the current protocol.");
        }
    }

    private async Task<Guid> ResolvePatientReferenceAsync(Guid reference, CancellationToken cancellationToken)
    {
        var patients = await treatmentCentre.GetPatientsAsync(cancellationToken);
        foreach (var patient in patients)
        {
            if (ToPatientReference(patient.Uid) == reference)
            {
                return patient.Uid;
            }
        }

        throw new InvalidOperationException("The patient reference is stale. Refresh the terminal and try again.");
    }

    private TerminalPatient? ToTerminalPatient(Patient? patient) => patient is null
        ? null
        : new TerminalPatient(ToPatientReference(patient.Uid), patient.PatientNumber, patient.AddedAt);

    private Guid ToPatientReference(Guid patientUid)
    {
        using var hmac = new HMACSHA256(_patientReferenceKey);
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(patientUid.ToString("N")));
        return new Guid(hash.AsSpan(0, 16));
    }

    private static Guid Required(Guid? value, string name) =>
        value is { } id && id != Guid.Empty ? id : throw new ArgumentException($"A valid {name} identifier is required.");

    private static void ValidateRequest(TerminalCommandRequest request)
    {
        if (request.CreatedAt is { } createdAt && createdAt > DateTimeOffset.UtcNow.AddMinutes(5))
        {
            throw new ArgumentException("The command timestamp is too far in the future.");
        }

        if (request.Name?.Length > 80 || request.Note?.Length > 500 || request.Location?.Length > 200
            || request.DischargeRoute?.Length > 100 || request.DischargeOutcome?.Length > 100)
        {
            throw new ArgumentException("One or more command fields exceed their allowed length.");
        }
    }

    private static string? AuditTarget(TerminalCommandRequest request) => request.Kind switch
    {
        TerminalCommandKind.AddPatientToStation => request.TargetId is { } station ? $"station:{station:N}" : null,
        TerminalCommandKind.AddPatientToMobileTeam or TerminalCommandKind.UpdateMobileTeam
            or TerminalCommandKind.DeployMobileTeam or TerminalCommandKind.UpdateMobileTeamLocation
            or TerminalCommandKind.StandDownMobileTeam => request.TargetId is { } team ? $"mobile-team:{team:N}" : null,
        TerminalCommandKind.MovePatient => request.SecondaryId is { } destination ? $"destination:{destination:N}" : null,
        TerminalCommandKind.AddMobileTeam => "mobile-team:new",
        TerminalCommandKind.DischargePatient => "patient:opaque",
        _ => null
    };
}
