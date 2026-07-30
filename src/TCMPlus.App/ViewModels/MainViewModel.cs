using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TCMPlus.App.LanDisplay;
using TCMPlus.App.TerminalNetworking;
using TCMPlus.App.Updates;
using TCMPlus.Domain.Models;
using TCMPlus.Domain.Persistence;
using TCMPlus.Domain.Services;
using TCMPlus.Infrastructure.Networking;
using TCMPlus.Protocol;

namespace TCMPlus.App.ViewModels;

public partial class MainViewModel : ViewModelBase
{
    private readonly ITreatmentCentreService _treatmentCentreService;
    private readonly ITcSettingsRepository _settingsRepository;
    private readonly IShiftPinService _shiftPinService;
    private readonly IAppSettingsRepository _appSettingsRepository;
    private readonly LanDisplayServer _lanDisplayServer;
    private readonly TerminalRuntimeContext _runtime;
    private readonly IAppUpdateService _appUpdateService;
    private AppSettings? _appSettings;
    private TcSessionSettings? _sessionSettings;
    private readonly DispatcherTimer _clockTimer;
    private readonly DispatcherTimer _bannerTimer;
    private readonly DispatcherTimer _terminalRefreshTimer;
    private bool _terminalRefreshInProgress;

    public MainViewModel(
        ITreatmentCentreService treatmentCentreService,
        ITcSettingsRepository settingsRepository,
        IShiftPinService shiftPinService,
        IAppSettingsRepository appSettingsRepository,
        LanDisplayServer lanDisplayServer,
        IAppUpdateService appUpdateService,
        SessionDescriptor session,
        TerminalRuntimeContext runtime)
    {
        _treatmentCentreService = treatmentCentreService;
        _settingsRepository = settingsRepository;
        _shiftPinService = shiftPinService;
        _appSettingsRepository = appSettingsRepository;
        _lanDisplayServer = lanDisplayServer;
        _appUpdateService = appUpdateService;
        _runtime = runtime;
        Session = session;
        _shiftName = session.ShiftName;
        _clockTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _clockTimer.Tick += (_, _) => RefreshClock();
        _clockTimer.Start();
        _bannerTimer = new DispatcherTimer();
        _bannerTimer.Tick += (_, _) => { IsBannerVisible = false; _bannerTimer.Stop(); };
        _terminalRefreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _terminalRefreshTimer.Tick += async (_, _) => await RefreshTerminalAsync();
        if (_runtime.RemoteService is not null)
        {
            _runtime.RemoteService.QueueChanged += (_, _) => Dispatcher.UIThread.Post(() => _ = UpdateTerminalQueueStateAsync());
        }
        RefreshClock();
    }

    public event EventHandler? AddStationRequested;
    public event Action<StationViewModel>? NewPatientRequested;
    public event Action<StationViewModel, StationViewModel>? PatientSwapConfirmationRequested;
    public event Action<StationViewModel>? DischargeRequested;
    public event Action<StationViewModel>? StationDeletionRequested;
    public event Action<PatientViewModel>? PatientDeletionRequested;
    public event Action<int>? BulkComplaintRequested;
    public event EventHandler? AddMobileTeamRequested;
    public event Action<MobileTeamViewModel>? MobileTeamDeployRequested;
    public event Action<MobileTeamViewModel>? MobileTeamLocationRequested;
    public event Action<MobileTeamViewModel>? MobileTeamPatientRequested;
    public event Action<MobileTeamViewModel>? MobileTeamStandDownRequested;
    public event Action<MobileTeamViewModel>? MobileTeamDischargeRequested;
    public event Action<MobileTeamViewModel>? MobileTeamEditRequested;
    public event Action<MobileTeamViewModel>? MobileTeamDeletionRequested;
    public event EventHandler? SessionSwitchRequested;
    public event Action<ExternalDisplayMode>? ExternalDisplayRequested;
    public event EventHandler? SessionLockRequested;
    public event EventHandler? SessionUnlockRequested;

    public SessionDescriptor Session { get; }
    public ObservableCollection<StationViewModel> Stations { get; } = [];
    public ObservableCollection<MobileTeamViewModel> MobileTeams { get; } = [];
    public ObservableCollection<DashboardChartSlice> ComplaintBreakdown { get; } = [];
    public ObservableCollection<DashboardChartSlice> DischargeRouteBreakdown { get; } = [];
    public ObservableCollection<DashboardChartPoint> ThroughputPoints { get; } = [];
    public ObservableCollection<DashboardChartPoint> DischargeDurationPoints { get; } = [];
    public ObservableCollection<DashboardChartPoint> OccupancyPoints { get; } = [];
    public ObservableCollection<DashboardChartPoint> CumulativeArrivalPoints { get; } = [];
    public ObservableCollection<string> DischargeRoutes { get; } = [];
    public IReadOnlyList<string> DischargeOutcomes { get; } = DischargeOutcomeOptions.Defaults;
    public ObservableCollection<PatientViewModel> Patients { get; } = [];
    public ObservableCollection<LanDisplayAddress> LanDisplayAddresses { get; } = [];
    public ObservableCollection<string> TerminalHostAddresses { get; } = [];
    public ObservableCollection<TerminalRegistration> RegisteredTerminals { get; } = [];
    public ObservableCollection<TerminalAuditEntry> TerminalAuditEntries { get; } = [];

    [ObservableProperty] private TcArea _selectedArea = TcArea.Manager;
    [ObservableProperty] private TcPage _selectedPage = TcPage.Map;
    [ObservableProperty] private bool _isEditMode;
    [ObservableProperty] private bool _isPatientEditMode;
    [ObservableProperty] private bool _quickEntry;
    [ObservableProperty] private GridDensity _gridDensity = GridDensity.Compact;
    [ObservableProperty] private SettingsPage _settingsPage = SettingsPage.General;
    [ObservableProperty] private string _newDischargeRoute = "";
    [ObservableProperty] private string _newPin = "";
    [ObservableProperty] private bool _isChangingPin;
    [ObservableProperty] private string _shiftName = "";
    [ObservableProperty] private string _pinStatusText = "No shift PIN set.";
    [ObservableProperty] private int _availableStations;
    [ObservableProperty] private int _occupiedStations;
    [ObservableProperty] private int _patientsSeenThisShift;
    [ObservableProperty] private string _currentTimeText = "";
    [ObservableProperty] private bool _isLocked;
    [ObservableProperty] private double _lockBlurRadius = 10d;
    [ObservableProperty] private string _unlockDigit1 = "";
    [ObservableProperty] private string _unlockDigit2 = "";
    [ObservableProperty] private string _unlockDigit3 = "";
    [ObservableProperty] private string _unlockDigit4 = "";
    [ObservableProperty] private string _unlockDigit5 = "";
    [ObservableProperty] private string _unlockDigit6 = "";
    [ObservableProperty] private string _lockMessage = "Enter the shift PIN to continue.";
    [ObservableProperty] private bool _isBannerVisible;
    [ObservableProperty] private string _bannerText = "";
    [ObservableProperty] private NotificationKind _notificationKind = NotificationKind.Info;
    [ObservableProperty] private string _averageDischargeText = "No discharges yet";
    [ObservableProperty] private string _averageThroughputText = "No discharges yet";
    [ObservableProperty] private bool _hasComplaintBreakdown;
    [ObservableProperty] private bool _hasDischargeRouteBreakdown;
    [ObservableProperty] private bool _hasThroughput;
    [ObservableProperty] private bool _hasDischargeDurations;
    [ObservableProperty] private bool _isLanDisplayRunning;
    [ObservableProperty] private string _lanDisplayPin = "";
    [ObservableProperty] private string _lanDisplayStatus = "The LAN web display is off.";
    [ObservableProperty] private bool _isTerminalHostRunning;
    [ObservableProperty] private string _terminalHostStatus = "App-to-app terminal hosting is off.";
    [ObservableProperty] private string _terminalCertificateFingerprint = "";
    [ObservableProperty] private string _newTerminalName = "";
    [ObservableProperty] private string _newTerminalPassword = "";
    [ObservableProperty] private int _pendingTerminalCommands;
    [ObservableProperty] private int _rejectedTerminalCommands;
    [ObservableProperty] private string _terminalConnectionStatus = "";
    [ObservableProperty] private string _terminalQueueReviewText = "";
    [ObservableProperty] private string _updateStatusText = "Updates can be installed from the Start Shift screen.";

    public bool HasNoStations => Stations.Count == 0;
    public bool HasNoMobileTeams => MobileTeams.Count == 0;
    public bool IsDashboard => SelectedArea == TcArea.Dashboard;
    public bool IsManager => SelectedArea == TcArea.Manager;
    public bool IsSettings => SelectedArea == TcArea.Settings;
    public bool IsSettingsGeneral => SettingsPage == SettingsPage.General;
    public bool IsSettingsOperations => SettingsPage == SettingsPage.Operations;
    public bool IsSettingsDisplays => SettingsPage == SettingsPage.Displays;
    public bool IsNotificationInfo => NotificationKind == NotificationKind.Info;
    public bool IsNotificationWarning => NotificationKind == NotificationKind.Warning;
    public bool IsNotificationError => NotificationKind == NotificationKind.Error;
    public double ActiveBlurRadius => IsLocked ? LockBlurRadius : 0d;
    public bool IsMapPage => IsManager && SelectedPage == TcPage.Map;
    public bool IsTablesPage => IsManager && SelectedPage == TcPage.Tables;
    public bool IsPatientsPage => IsManager && SelectedPage == TcPage.Patients;
    public bool IsSetupPage => IsManager && SelectedPage == TcPage.Setup;
    public bool HasNoPatients => Patients.Count == 0;
    public bool HasNoComplaintBreakdown => !HasComplaintBreakdown;
    public bool HasNoDischargeRouteBreakdown => !HasDischargeRouteBreakdown;
    public bool HasNoThroughput => !HasThroughput;
    public bool HasNoDischargeDurations => !HasDischargeDurations;
    public bool HasLanDisplayAddresses => LanDisplayAddresses.Count > 0;
    public bool IsLanDisplayStopped => !IsLanDisplayRunning;
    public bool IsTerminalHostStopped => !IsTerminalHostRunning;
    public bool IsTerminal => _runtime.IsTerminal;
    public bool CanAdministerHost => !_runtime.IsTerminal;
    public bool CanLock => !_runtime.IsTerminal;
    public bool HasRejectedTerminalCommands => RejectedTerminalCommands > 0;
    public bool HasPendingTerminalCommands => PendingTerminalCommands > 0;
    public string InstanceModeText => IsTerminal
        ? $"Terminal: {_runtime.TerminalName} - {_runtime.HostAddress}"
        : "Authoritative host";
    public int DeployedMobileTeams => MobileTeams.Count(team => team.IsDeployed);
    public string ApplicationVersion => typeof(MainViewModel).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
        .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().SingleOrDefault()?.InformationalVersion ?? "";
    public string EditModeText => IsEditMode ? "Finish editing" : "Edit Treatment Centre";
    public string PatientEditModeText => IsPatientEditMode ? "Finish editing" : "Edit patients";
    public string MapStatusText => IsEditMode ? "Drag a station from anywhere except a corner. Use any corner to resize, or delete an available station from its card." : "Click an available station to add a patient. Drag a patient counter to transfer.";
    public int TotalStations => AvailableStations + OccupiedStations;
    public double GridPixelSize => GridDensity switch { GridDensity.Standard => 20d, GridDensity.Dense => 16d, _ => 24d };

    public async Task InitializeAsync()
    {
        try
        {
            foreach (var item in await _treatmentCentreService.GetSnapshotAsync()) AddViewModel(item.Station, item.CurrentPatient);
            foreach (var item in await _treatmentCentreService.GetMobileTeamsAsync()) AddMobileTeamViewModel(item.Team, item.CurrentPatient);
            var settings = await _settingsRepository.GetAsync();
            _sessionSettings = settings;
            ShiftName = string.IsNullOrWhiteSpace(settings.ShiftName) ? Session.ShiftName : settings.ShiftName;
            PinStatusText = settings.HasShiftPin ? "A shift PIN is stored for this session." : "No shift PIN set.";
            QuickEntry = settings.QuickEntry;
            GridDensity = settings.GridDensity;
            _appSettings = await _appSettingsRepository.GetAsync();
            LockBlurRadius = Math.Clamp(_appSettings.LockBlurRadius, 4d, 20d);
            foreach (var route in _appSettings.DischargeRoutes) DischargeRoutes.Add(route);
            await RefreshSummaryAsync();
            await RefreshDashboardAsync();
            if (Stations.Count == 0) Notify("Edit the treatment centre to add the first station.");
            if (_runtime.IsTerminal)
            {
                await UpdateTerminalQueueStateAsync();
                TerminalConnectionStatus = $"Connected securely to {_runtime.HostAddress}.";
                _terminalRefreshTimer.Start();
            }
            else
            {
                await RefreshRegisteredTerminalsAsync();
            }
        }
        catch (Exception exception) { Notify($"Unable to load this session: {exception.Message}", true); }
    }

    [RelayCommand] private async Task ShowDashboardAsync() { ClearPatientEdits(); SelectedArea = TcArea.Dashboard; await RefreshDashboardAsync(); }
    [RelayCommand] private void ShowManager() => SelectedArea = TcArea.Manager;
    [RelayCommand] private void ShowMap() { SelectedArea = TcArea.Manager; SelectedPage = TcPage.Map; }
    [RelayCommand] private void ShowTables() { SelectedArea = TcArea.Manager; SelectedPage = TcPage.Tables; }
    [RelayCommand]
    private async Task ShowPatientsAsync()
    {
        if (!CanAdministerHost) return;
        SelectedArea = TcArea.Manager;
        SelectedPage = TcPage.Patients;
        try { await RefreshPatientsAsync(); }
        catch (Exception exception) { Notify($"Unable to load patients: {exception.Message}", true); }
    }
    [RelayCommand] private void ShowSetup() { SelectedArea = TcArea.Manager; SelectedPage = TcPage.Setup; }
    [RelayCommand] private void ToggleEditMode() { if (CanAdministerHost) IsEditMode = !IsEditMode; }
    [RelayCommand] private void TogglePatientEditMode() { if (CanAdministerHost) IsPatientEditMode = !IsPatientEditMode; }
    [RelayCommand]
    private void RequestBulkComplaint()
    {
        var selectedCount = Patients.Count(patient => patient.IsSelected);
        if (selectedCount == 0)
        {
            Notify("Select at least one patient.", true);
            return;
        }

        BulkComplaintRequested?.Invoke(selectedCount);
    }
    [RelayCommand] private void RequestAddStation() { if (CanAdministerHost) AddStationRequested?.Invoke(this, EventArgs.Empty); }
    [RelayCommand] private void RequestAddMobileTeam() => AddMobileTeamRequested?.Invoke(this, EventArgs.Empty);
    [RelayCommand] private void ShowSettings() { if (CanAdministerHost) { ClearPatientEdits(); SelectedArea = TcArea.Settings; } }
    [RelayCommand] private void ShowSettingsGeneral() => SettingsPage = SettingsPage.General;
    [RelayCommand] private void ShowSettingsOperations() => SettingsPage = SettingsPage.Operations;
    [RelayCommand] private void ShowSettingsDisplays() => SettingsPage = SettingsPage.Displays;

    [RelayCommand]
    private async Task CheckForUpdatesAsync()
    {
        UpdateStatusText = "Checking for updates...";
        var result = await _appUpdateService.CheckForUpdatesAsync();
        UpdateStatusText = result.StatusText;
        if (result.Status == AppUpdateStatus.Available)
        {
            Notify($"TCM+ {result.Version} is available. Safely exit this session, then update from the Start Shift screen.");
        }
    }

    [RelayCommand]
    private async Task AddDischargeRouteAsync()
    {
        var route = NewDischargeRoute.Trim();
        if (string.IsNullOrWhiteSpace(route) || DischargeRoutes.Contains(route, StringComparer.OrdinalIgnoreCase)) return;
        DischargeRoutes.Add(route); NewDischargeRoute = ""; await SaveAppSettingsAsync(DischargeRoutes, (await _appSettingsRepository.GetAsync()).ExternalDisplayMode);
    }

    [RelayCommand]
    private async Task RemoveDischargeRouteAsync(string? route)
    {
        if (string.IsNullOrWhiteSpace(route) || DischargeRoutes.Count <= 1) return;
        DischargeRoutes.Remove(route); await SaveAppSettingsAsync(DischargeRoutes, (await _appSettingsRepository.GetAsync()).ExternalDisplayMode);
    }
    [RelayCommand] private void OpenExternalDisplay(string mode) => ExternalDisplayRequested?.Invoke(string.Equals(mode, "Map", StringComparison.OrdinalIgnoreCase) ? ExternalDisplayMode.Map : ExternalDisplayMode.Dashboard);
    [RelayCommand]
    private async Task StartLanDisplayAsync()
    {
        try
        {
            var access = await _lanDisplayServer.StartAsync();
            LanDisplayAddresses.Clear();
            foreach (var address in access.Addresses) LanDisplayAddresses.Add(address);
            LanDisplayPin = access.ViewerPin;
            IsLanDisplayRunning = true;
            LanDisplayStatus = "LAN web display is running. Enter the viewer PIN in each browser.";
            OnPropertyChanged(nameof(HasLanDisplayAddresses));
            Notify("LAN web display started.");
        }
        catch (Exception exception)
        {
            LanDisplayStatus = $"Could not start the LAN web display: {exception.Message}";
            Notify(LanDisplayStatus, true);
        }
    }
    [RelayCommand] private async Task StopLanDisplayAsync() => await StopLanDisplayForSessionAsync();

    [RelayCommand]
    private async Task StartTerminalHostAsync()
    {
        if (_runtime.HostServer is null) return;
        try
        {
            var access = await _runtime.HostServer.StartAsync();
            TerminalHostAddresses.Clear();
            foreach (var address in access.Addresses) TerminalHostAddresses.Add(address);
            TerminalCertificateFingerprint = access.CertificateFingerprint;
            IsTerminalHostRunning = true;
            TerminalHostStatus = $"Secure app-to-app hosting is running with protocol v{access.ProtocolVersion}.";
            OnPropertyChanged(nameof(IsTerminalHostStopped));
            await RefreshRegisteredTerminalsAsync();
            Notify("Secure terminal hosting started.");
        }
        catch (Exception exception)
        {
            TerminalHostStatus = $"Could not start terminal hosting: {exception.Message}";
            Notify(TerminalHostStatus, true);
        }
    }

    [RelayCommand]
    private async Task StopTerminalHostAsync()
    {
        if (_runtime.HostServer is null) return;
        await _runtime.HostServer.StopAsync();
        TerminalHostAddresses.Clear();
        TerminalCertificateFingerprint = "";
        NewTerminalPassword = "";
        IsTerminalHostRunning = false;
        TerminalHostStatus = "App-to-app terminal hosting is off. All temporary terminal sessions were revoked.";
        OnPropertyChanged(nameof(IsTerminalHostStopped));
        await RefreshRegisteredTerminalsAsync();
    }

    [RelayCommand]
    private async Task CreateTerminalCredentialAsync()
    {
        if (_runtime.HostServer is null || !IsTerminalHostRunning)
        {
            Notify("Start terminal hosting before creating a terminal credential.", true);
            return;
        }

        try
        {
            var credential = await _runtime.HostServer.CreateTerminalAsync(NewTerminalName);
            NewTerminalName = "";
            NewTerminalPassword = credential.Password;
            await RefreshRegisteredTerminalsAsync();
            Notify($"Temporary access created for {credential.Registration.Name}. The password is shown once.");
        }
        catch (Exception exception)
        {
            Notify(exception.Message, true);
        }
    }

    [RelayCommand]
    private async Task RevokeTerminalAsync(TerminalRegistration? terminal)
    {
        if (_runtime.HostServer is null || terminal is null) return;
        await _runtime.HostServer.RevokeTerminalAsync(terminal.Id);
        await RefreshRegisteredTerminalsAsync();
        Notify($"{terminal.Name} revoked.");
    }

    [RelayCommand]
    private async Task RefreshTerminalAuditAsync()
    {
        if (_runtime.HostServer is null) return;
        TerminalAuditEntries.Clear();
        foreach (var entry in await _runtime.HostServer.GetAuditAsync()) TerminalAuditEntries.Add(entry);
    }

    [RelayCommand]
    private async Task AcknowledgeRejectedTerminalCommandsAsync()
    {
        if (_runtime.RemoteService is null) return;
        await _runtime.RemoteService.AcknowledgeRejectedCommandsAsync();
        await UpdateTerminalQueueStateAsync();
    }

    [RelayCommand] private void RequestSessionSwitch() => SessionSwitchRequested?.Invoke(this, EventArgs.Empty);
    [RelayCommand] private async Task SaveQuickEntryAsync() => await SaveSessionOptionsAsync();
    [RelayCommand] private void SetCompactDensity() => SetGridDensity(GridDensity.Compact);
    [RelayCommand] private void SetStandardDensity() => SetGridDensity(GridDensity.Standard);
    [RelayCommand] private void SetDenseDensity() => SetGridDensity(GridDensity.Dense);
    [RelayCommand] private void BeginPinChange() => IsChangingPin = true;

    [RelayCommand]
    private void Lock()
    {
        if (!CanLock) return;
        ClearUnlockPin();
        LockMessage = "Enter the shift PIN to continue.";
        SessionLockRequested?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private async Task UnlockAsync()
    {
        try
        {
            var settings = _sessionSettings ?? await _settingsRepository.GetAsync();
            if (_shiftPinService.Verify(UnlockPin, settings)) { SessionUnlockRequested?.Invoke(this, EventArgs.Empty); return; }
            LockMessage = "That PIN does not match this shift."; ClearUnlockPin();
        }
        catch (Exception exception)
        {
            LockMessage = $"Unable to verify the shift PIN: {exception.Message}";
            ClearUnlockPin();
        }
    }

    public async Task CreateStationAsync(StationDraft draft)
    {
        try { var station = await _treatmentCentreService.AddStationAsync(draft.Name, draft.Type); AddViewModel(station, null); await RefreshSummaryAsync(); Notify($"{station.Name} added."); }
        catch (Exception exception) { Notify(exception.Message, true); }
    }

    public async Task CreateMobileTeamAsync(MobileTeamDraft draft)
    {
        try
        {
            var team = await _treatmentCentreService.AddMobileTeamAsync(draft.Callsign, draft.Note);
            AddMobileTeamViewModel(team, null);
            SortMobileTeams();
            Notify($"{team.Callsign} added.");
        }
        catch (Exception exception) { Notify(exception.Message, true); }
    }

    public async Task UpdateMobileTeamAsync(MobileTeamViewModel team, MobileTeamDraft draft)
    {
        try
        {
            var updated = await _treatmentCentreService.UpdateMobileTeamAsync(team.Id, draft.Callsign, draft.Note);
            team.Apply(updated, team.CurrentPatient);
            SortMobileTeams();
            Notify($"{updated.Callsign} saved.");
        }
        catch (Exception exception) { Notify(exception.Message, true); }
    }

    public async Task DeployMobileTeamAsync(MobileTeamViewModel team, string? location)
    {
        try
        {
            var updated = await _treatmentCentreService.DeployMobileTeamAsync(team.Id, location);
            team.Apply(updated, team.CurrentPatient);
            OnPropertyChanged(nameof(DeployedMobileTeams));
            Notify($"{team.Callsign} deployed.");
        }
        catch (Exception exception) { Notify(exception.Message, true); }
    }

    public async Task UpdateMobileTeamLocationAsync(MobileTeamViewModel team, string? location)
    {
        try
        {
            var updated = await _treatmentCentreService.UpdateMobileTeamLocationAsync(team.Id, location);
            team.Apply(updated, team.CurrentPatient);
            Notify($"{team.Callsign} location updated.");
        }
        catch (Exception exception) { Notify(exception.Message, true); }
    }

    public async Task ConfirmDeleteMobileTeamAsync(MobileTeamViewModel team)
    {
        try
        {
            await _treatmentCentreService.DeleteMobileTeamAsync(team.Id);
            MobileTeams.Remove(team);
            OnPropertyChanged(nameof(HasNoMobileTeams));
            OnPropertyChanged(nameof(DeployedMobileTeams));
            Notify($"{team.Callsign} removed.");
        }
        catch (Exception exception) { Notify(exception.Message, true); }
    }

    public async Task StandDownMobileTeamAsync(MobileTeamViewModel team)
    {
        try
        {
            var updated = await _treatmentCentreService.StandDownMobileTeamAsync(team.Id);
            team.Apply(updated, null);
            OnPropertyChanged(nameof(DeployedMobileTeams));
            Notify($"{team.Callsign} stood down.");
        }
        catch (Exception exception) { Notify(exception.Message, true); }
    }

    public async Task SubmitNewMobileTeamPatientAsync(MobileTeamViewModel team, NewPatientDraft draft)
    {
        try
        {
            team.CurrentPatient = await _treatmentCentreService.AddPatientToMobileTeamAsync(team.Id, draft.PresentingComplaint);
            await RefreshOperationalDataAsync();
            Notify($"{team.PatientCounterText} added to {team.Callsign}.");
        }
        catch (Exception exception) { Notify(exception.Message, true); }
    }

    public async Task CompleteMobileTeamDischargeAsync(MobileTeamViewModel team, string? route, string? outcome, bool standDown)
    {
        if (team.CurrentPatient is not { } patient)
        {
            return;
        }

        try
        {
            var patientNumber = patient.PatientNumber;
            await _treatmentCentreService.DischargeAssignedPatientAsync(patient.Uid, route, outcome);
            team.CurrentPatient = null;
            if (standDown)
            {
                var updated = await _treatmentCentreService.StandDownMobileTeamAsync(team.Id);
                team.Apply(updated, null);
                OnPropertyChanged(nameof(DeployedMobileTeams));
            }
            await RefreshOperationalDataAsync();
            Notify(standDown ? $"{team.Callsign} patient discharged and team stood down." : $"Patient {patientNumber} discharged.");
        }
        catch (Exception exception) { Notify(exception.Message, true); }
    }

    public async Task TransferTeamPatientAndStandDownAsync(MobileTeamViewModel team, Guid stationId)
    {
        if (team.CurrentPatient is not { } patient) return;
        try
        {
            await _treatmentCentreService.MovePatientAsync(patient.Uid, new PatientAssignment(PatientAssignmentKind.Station, stationId), false);
            await RefreshAssignmentsAsync();
            var updated = await _treatmentCentreService.StandDownMobileTeamAsync(team.Id);
            team.Apply(updated, null);
            OnPropertyChanged(nameof(DeployedMobileTeams));
            await RefreshOperationalDataAsync();
            Notify($"{team.Callsign} patient transferred and team stood down.");
        }
        catch (Exception exception) { Notify(exception.Message, true); }
    }

    public async Task StopLanDisplayForSessionAsync()
    {
        await _lanDisplayServer.StopAsync();
        LanDisplayAddresses.Clear();
        LanDisplayPin = "";
        IsLanDisplayRunning = false;
        LanDisplayStatus = "The LAN web display is off.";
        OnPropertyChanged(nameof(HasLanDisplayAddresses));
    }

    public void StopUiTimersForSession()
    {
        _terminalRefreshTimer.Stop();
        _clockTimer.Stop();
        _bannerTimer.Stop();
    }

    public async Task SubmitNewPatientAsync(StationViewModel station, NewPatientDraft draft)
    {
        try { station.CurrentPatient = await _treatmentCentreService.AddPatientAsync(station.Id, draft.PresentingComplaint); await RefreshOperationalDataAsync(); Notify($"{station.PatientCounterText} added to {station.Name}."); }
        catch (Exception exception) { Notify(exception.Message, true); }
    }

    public async Task ConfirmPatientSwapAsync(StationViewModel source, StationViewModel destination)
    {
        try { await MovePatientAsync(source, destination, true); Notify($"Patients swapped between {source.Name} and {destination.Name}."); }
        catch (Exception exception) { Notify(exception.Message, true); }
    }

    [RelayCommand]
    private async Task SaveShiftPinAsync()
    {
        if (string.IsNullOrWhiteSpace(ShiftName)) { PinStatusText = "Enter a shift name."; return; }
        var settings = await _settingsRepository.GetAsync();
        if (!string.IsNullOrWhiteSpace(NewPin))
        {
            if (!_shiftPinService.IsValidFormat(NewPin)) { PinStatusText = "Enter exactly six digits when changing the PIN."; return; }
            settings = _shiftPinService.CreateSettings(NewPin);
        }
        await _settingsRepository.SaveAsync(settings with { ShiftName = ShiftName.Trim(), GridDensity = GridDensity });
        _sessionSettings = settings with { ShiftName = ShiftName.Trim(), GridDensity = GridDensity };
        NewPin = ""; IsChangingPin = false; PinStatusText = "Shift details saved for this session.";
    }

    partial void OnSelectedAreaChanged(TcArea value) => RefreshAreaProperties();
    partial void OnIsLanDisplayRunningChanged(bool value) => OnPropertyChanged(nameof(IsLanDisplayStopped));
    partial void OnIsTerminalHostRunningChanged(bool value) => OnPropertyChanged(nameof(IsTerminalHostStopped));
    partial void OnPendingTerminalCommandsChanged(int value) => OnPropertyChanged(nameof(HasPendingTerminalCommands));
    partial void OnRejectedTerminalCommandsChanged(int value) => OnPropertyChanged(nameof(HasRejectedTerminalCommands));
    partial void OnIsLockedChanged(bool value) => OnPropertyChanged(nameof(ActiveBlurRadius));
    partial void OnLockBlurRadiusChanged(double value)
    {
        OnPropertyChanged(nameof(ActiveBlurRadius));
        if (_appSettings is not null) _ = SaveLockBlurAsync(value);
    }
    partial void OnSettingsPageChanged(SettingsPage value) { OnPropertyChanged(nameof(IsSettingsGeneral)); OnPropertyChanged(nameof(IsSettingsOperations)); OnPropertyChanged(nameof(IsSettingsDisplays)); }
    partial void OnNotificationKindChanged(NotificationKind value) { OnPropertyChanged(nameof(IsNotificationInfo)); OnPropertyChanged(nameof(IsNotificationWarning)); OnPropertyChanged(nameof(IsNotificationError)); }
    partial void OnSelectedPageChanged(TcPage value)
    {
        if (value != TcPage.Patients) ClearPatientEdits();
        RefreshAreaProperties();
    }
    partial void OnIsEditModeChanged(bool value)
    {
        foreach (var station in Stations) station.IsEditMode = value;
        OnPropertyChanged(nameof(EditModeText)); OnPropertyChanged(nameof(MapStatusText));
    }
    partial void OnIsPatientEditModeChanged(bool value)
    {
        foreach (var patient in Patients) patient.IsEditMode = value;
        OnPropertyChanged(nameof(PatientEditModeText));
    }

    partial void OnQuickEntryChanged(bool value)
    {
        if (CanAdministerHost) _ = SaveSessionOptionsAsync();
    }
    partial void OnGridDensityChanged(GridDensity value) { foreach (var station in Stations) station.GridSizePixels = GridPixelSize; OnPropertyChanged(nameof(GridPixelSize)); }

    private void RefreshAreaProperties()
    {
        OnPropertyChanged(nameof(IsDashboard)); OnPropertyChanged(nameof(IsManager)); OnPropertyChanged(nameof(IsSettings)); OnPropertyChanged(nameof(IsMapPage)); OnPropertyChanged(nameof(IsTablesPage)); OnPropertyChanged(nameof(IsPatientsPage)); OnPropertyChanged(nameof(IsSetupPage));
    }

    private void SetGridDensity(GridDensity density)
    {
        if (density < GridDensity)
        {
            NotifyWarning("Map density can only be increased after stations have been placed.");
            return;
        }
        GridDensity = density;
    }

    private void AddViewModel(Station station, Patient? patient)
    {
        var viewModel = new StationViewModel(station, patient, SaveStationAsync, DeleteStationAsync, RequestNewPatient, RequestDischarge, CommitGeometryAsync, RequestPatientDropAsync) { IsEditMode = IsEditMode, GridSizePixels = GridPixelSize };
        viewModel.PropertyChanged += async (_, args) => { if (args.PropertyName is nameof(StationViewModel.CurrentPatient)) await RefreshSummaryAsync(); };
        Stations.Add(viewModel); OnPropertyChanged(nameof(HasNoStations));
    }

    private void AddMobileTeamViewModel(MobileTeam team, Patient? patient)
    {
        var viewModel = new MobileTeamViewModel(
            team,
            patient,
            RequestMobileTeamDeploy,
            RequestMobileTeamLocation,
            RequestMobileTeamPatient,
            RequestMobileTeamStandDown,
            RequestMobileTeamDischarge,
            RequestMobileTeamEdit,
            RequestMobileTeamDeletion,
            RequestMobileTeamPatientDropAsync,
            CanAdministerHost);
        viewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(MobileTeamViewModel.IsDeployed))
            {
                OnPropertyChanged(nameof(DeployedMobileTeams));
            }
        };
        MobileTeams.Add(viewModel);
        OnPropertyChanged(nameof(HasNoMobileTeams));
        OnPropertyChanged(nameof(DeployedMobileTeams));
    }

    private void RequestMobileTeamDeploy(MobileTeamViewModel team) => MobileTeamDeployRequested?.Invoke(team);
    private void RequestMobileTeamLocation(MobileTeamViewModel team) => MobileTeamLocationRequested?.Invoke(team);
    private void RequestMobileTeamStandDown(MobileTeamViewModel team) => MobileTeamStandDownRequested?.Invoke(team);
    private void RequestMobileTeamDischarge(MobileTeamViewModel team) => MobileTeamDischargeRequested?.Invoke(team);
    private void RequestMobileTeamEdit(MobileTeamViewModel team) => MobileTeamEditRequested?.Invoke(team);
    private void RequestMobileTeamDeletion(MobileTeamViewModel team) => MobileTeamDeletionRequested?.Invoke(team);
    private void RequestMobileTeamPatient(MobileTeamViewModel team)
    {
        if (QuickEntry)
        {
            _ = SubmitNewMobileTeamPatientAsync(team, new NewPatientDraft(null));
            return;
        }
        MobileTeamPatientRequested?.Invoke(team);
    }

    private void RequestNewPatient(StationViewModel station)
    {
        if (QuickEntry) { _ = SubmitNewPatientAsync(station, new NewPatientDraft(null)); return; }
        NewPatientRequested?.Invoke(station);
    }
    private void RequestDischarge(StationViewModel station)
    {
        if (QuickEntry) { _ = CompleteDischargeAsync(station, null, null); return; }
        DischargeRequested?.Invoke(station);
    }
    private async Task RequestPatientDropAsync(StationViewModel destination, Guid patientUid)
    {
        var sourceStation = Stations.FirstOrDefault(station => station.CurrentPatient?.Uid == patientUid);
        if (sourceStation == destination) return;
        if (destination.IsOccupied && sourceStation is not null)
        {
            PatientSwapConfirmationRequested?.Invoke(sourceStation, destination);
            return;
        }
        if (destination.IsOccupied)
        {
            Notify("The destination station is occupied.", true);
            return;
        }
        try
        {
            await _treatmentCentreService.MovePatientAsync(patientUid, new PatientAssignment(PatientAssignmentKind.Station, destination.Id), false);
            await RefreshAssignmentsAsync();
            await RefreshOperationalDataAsync();
            Notify($"{destination.PatientCounterText} moved to {destination.Name}.");
        }
        catch (Exception exception) { Notify(exception.Message, true); }
    }

    private async Task RequestMobileTeamPatientDropAsync(MobileTeamViewModel destination, Guid patientUid)
    {
        if (!destination.CanAcceptPatientDrop)
        {
            Notify(destination.IsDeployed ? "This mobile team already has a patient." : "Deploy the mobile team before transferring a patient to it.", true);
            return;
        }

        try
        {
            await _treatmentCentreService.MovePatientAsync(patientUid, new PatientAssignment(PatientAssignmentKind.MobileTeam, destination.Id), false);
            await RefreshAssignmentsAsync();
            await RefreshOperationalDataAsync();
            Notify($"{destination.PatientCounterText} moved to {destination.Callsign}.");
        }
        catch (Exception exception) { Notify(exception.Message, true); }
    }

    private async Task MovePatientAsync(StationViewModel source, StationViewModel destination, bool swap)
    {
        var result = await _treatmentCentreService.MovePatientAsync(source.Id, destination.Id, swap);
        source.CurrentPatient = result.SwappedPatient;
        destination.CurrentPatient = result.SourcePatient;
        await RefreshOperationalDataAsync();
    }

    private void SortMobileTeams()
    {
        var ordered = MobileTeams.OrderBy(team => team.Callsign, StringComparer.OrdinalIgnoreCase).ToList();
        MobileTeams.Clear();
        foreach (var team in ordered) MobileTeams.Add(team);
    }

    private async Task SaveStationAsync(StationViewModel station)
    {
        try { await _treatmentCentreService.SaveStationAsync(station.ToDomain()); Notify($"{station.Name} saved."); }
        catch (Exception exception) { Notify(exception.Message, true); }
    }

    public async Task ReorderStationAsync(Guid sourceStationId, Guid targetStationId, bool placeAfter)
    {
        var originalOrder = Stations.ToList();
        try
        {
            var source = Stations.FirstOrDefault(station => station.Id == sourceStationId);
            var target = Stations.FirstOrDefault(station => station.Id == targetStationId);
            if (source is null || target is null || source == target)
            {
                return;
            }

            Stations.Remove(source);
            var targetIndex = Stations.IndexOf(target);
            Stations.Insert(targetIndex + (placeAfter ? 1 : 0), source);
            if (Stations.SequenceEqual(originalOrder))
            {
                return;
            }

            await _treatmentCentreService.ReorderStationsAsync(Stations.Select(station => station.Id).ToList());
            Notify("Station order saved.");
        }
        catch (Exception exception)
        {
            Stations.Clear();
            foreach (var station in originalOrder)
            {
                Stations.Add(station);
            }
            Notify(exception.Message, true);
        }
    }

    private async Task SavePatientAsync(PatientViewModel patient)
    {
        try
        {
            if (!patient.TryGetEditedTimes(out var addedAt, out var dischargedAt, out var error))
            {
                Notify(error!, true);
                return;
            }

            var updated = await _treatmentCentreService.UpdatePatientDetailsAsync(patient.Uid, addedAt, dischargedAt, patient.PresentingComplaint, patient.DischargeRoute, patient.DischargeOutcome);
            patient.AcceptSavedDetails(updated);
            var station = Stations.FirstOrDefault(item => item.CurrentPatient?.Uid == patient.Uid);
            if (station is not null)
            {
                station.CurrentPatient = updated;
            }
            var team = MobileTeams.FirstOrDefault(item => item.CurrentPatient?.Uid == patient.Uid);
            if (team is not null)
            {
                team.CurrentPatient = updated;
            }
            await RefreshDashboardAsync();
            Notify($"Patient {patient.PatientNumber} saved.");
        }
        catch (Exception exception) { Notify(exception.Message, true); }
    }

    private Task DeleteStationAsync(StationViewModel station)
    {
        StationDeletionRequested?.Invoke(station);
        return Task.CompletedTask;
    }

    public async Task ConfirmDeleteStationAsync(StationViewModel station)
    {
        try { await _treatmentCentreService.DeleteStationAsync(station.Id); Stations.Remove(station); await RefreshSummaryAsync(); await RefreshDashboardAsync(); OnPropertyChanged(nameof(HasNoStations)); Notify($"{station.Name} removed."); }
        catch (Exception exception) { Notify(exception.Message, true); }
    }

    private void RequestPatientDeletion(PatientViewModel patient) => PatientDeletionRequested?.Invoke(patient);

    public async Task ConfirmDeletePatientAsync(PatientViewModel patient)
    {
        try
        {
            await _treatmentCentreService.DeletePatientAsync(patient.Uid);
            var station = Stations.FirstOrDefault(item => item.CurrentPatient?.Uid == patient.Uid);
            if (station is not null) station.CurrentPatient = null;
            var team = MobileTeams.FirstOrDefault(item => item.CurrentPatient?.Uid == patient.Uid);
            if (team is not null) team.CurrentPatient = null;
            await RefreshOperationalDataAsync();
            Notify($"Patient {patient.PatientNumber} deleted.");
        }
        catch (Exception exception) { Notify(exception.Message, true); }
    }

    public async Task ApplyBulkComplaintAsync(string complaint)
    {
        try
        {
            var selected = Patients.Where(patient => patient.IsSelected).ToList();
            await _treatmentCentreService.UpdatePresentingComplaintAsync(selected.Select(patient => patient.Uid).ToList(), complaint);
            foreach (var patient in selected)
            {
                patient.AcceptBulkComplaint(complaint.Trim());
                patient.IsSelected = false;
            }
            await RefreshDashboardAsync();
            Notify($"Presenting complaint updated for {selected.Count} patient{(selected.Count == 1 ? "" : "s")}.");
        }
        catch (Exception exception) { Notify(exception.Message, true); }
    }

    public async Task CompleteDischargeAsync(StationViewModel station, string? route, string? outcome)
    {
        try { await _treatmentCentreService.DischargePatientAsync(station.Id, route, outcome); station.CurrentPatient = null; await RefreshOperationalDataAsync(); Notify($"{station.Name} is now available."); }
        catch (Exception exception) { Notify(exception.Message, true); }
    }

    public async Task SaveAppSettingsAsync(IEnumerable<string> dischargeRoutes, ExternalDisplayMode displayMode)
    {
        var settings = new AppSettings(dischargeRoutes.ToList(), displayMode, LockBlurRadius);
        await _appSettingsRepository.SaveAsync(settings);
        _appSettings = settings;
        DischargeRoutes.Clear();
        foreach (var route in (await _appSettingsRepository.GetAsync()).DischargeRoutes) DischargeRoutes.Add(route);
        Notify("Application settings saved.");
    }

    private async Task SaveLockBlurAsync(double value)
    {
        try
        {
            var settings = _appSettings! with { LockBlurRadius = Math.Clamp(value, 4d, 20d) };
            await _appSettingsRepository.SaveAsync(settings);
            _appSettings = settings;
        }
        catch { }
    }

    public void CompleteLock() => IsLocked = true;
    public void CompleteUnlock() { IsLocked = false; ClearUnlockPin(); }
    public void ReportPersistenceFailure(string message) => Notify(message, true);

    private async Task SaveSessionOptionsAsync()
    {
        var settings = await _settingsRepository.GetAsync();
        _sessionSettings = settings with { QuickEntry = QuickEntry, GridDensity = GridDensity };
        await _settingsRepository.SaveAsync(_sessionSettings);
    }

    private async Task CommitGeometryAsync(StationViewModel station, StationGeometry originalGeometry)
    {
        if (Stations.Any(other => other != station && Intersects(station, other))) { station.RestoreGeometry(originalGeometry); Notify("Stations cannot overlap. The previous position was restored.", true); return; }
        await SaveStationAsync(station);
    }

    private async Task RefreshOperationalDataAsync()
    {
        await RefreshSummaryAsync();
        await RefreshDashboardAsync();
        if (IsPatientsPage) await RefreshPatientsAsync();
    }

    private async Task RefreshTerminalAsync()
    {
        if (!_runtime.IsTerminal || _terminalRefreshInProgress)
        {
            return;
        }

        _terminalRefreshInProgress = true;
        try
        {
            await RefreshAssignmentsAsync();
            await RefreshOperationalDataAsync();
            TerminalConnectionStatus = $"Connected securely to {_runtime.HostAddress}.";
            await UpdateTerminalQueueStateAsync();
        }
        catch (Exception exception)
        {
            TerminalConnectionStatus = $"Connection unavailable: {exception.Message}";
            await UpdateTerminalQueueStateAsync();
        }
        finally
        {
            _terminalRefreshInProgress = false;
        }
    }

    private async Task RefreshRegisteredTerminalsAsync()
    {
        RegisteredTerminals.Clear();
        if (_runtime.HostServer is null)
        {
            return;
        }

        foreach (var terminal in await _runtime.HostServer.GetTerminalsAsync())
        {
            RegisteredTerminals.Add(terminal);
        }
    }

    private async Task UpdateTerminalQueueStateAsync()
    {
        if (_runtime.RemoteService is null)
        {
            PendingTerminalCommands = 0;
            RejectedTerminalCommands = 0;
            TerminalQueueReviewText = "";
            return;
        }

        PendingTerminalCommands = _runtime.RemoteService.PendingCommandCount;
        RejectedTerminalCommands = _runtime.RemoteService.RejectedCommandCount;
        var rejected = (await _runtime.RemoteService.GetQueuedCommandsAsync())
            .Where(command => command.State == QueuedTerminalCommandState.Rejected)
            .Take(3)
            .Select(command => $"{command.Command.Kind}: {command.RejectionReason}")
            .ToList();
        TerminalQueueReviewText = rejected.Count == 0
            ? ""
            : string.Join(Environment.NewLine, rejected);
    }

    private async Task RefreshAssignmentsAsync()
    {
        var stationSnapshots = await _treatmentCentreService.GetSnapshotAsync();
        var stationIds = stationSnapshots.Select(snapshot => snapshot.Station.Id).ToHashSet();
        foreach (var removed in Stations.Where(station => !stationIds.Contains(station.Id)).ToList())
        {
            Stations.Remove(removed);
        }
        foreach (var snapshot in stationSnapshots)
        {
            var existing = Stations.FirstOrDefault(station => station.Id == snapshot.Station.Id);
            if (existing is null)
            {
                AddViewModel(snapshot.Station, snapshot.CurrentPatient);
            }
            else
            {
                existing.Apply(snapshot.Station, snapshot.CurrentPatient);
            }
        }

        var teamSnapshots = await _treatmentCentreService.GetMobileTeamsAsync();
        var teamIds = teamSnapshots.Select(snapshot => snapshot.Team.Id).ToHashSet();
        foreach (var removed in MobileTeams.Where(team => !teamIds.Contains(team.Id)).ToList())
        {
            MobileTeams.Remove(removed);
        }
        foreach (var snapshot in teamSnapshots)
        {
            var existing = MobileTeams.FirstOrDefault(team => team.Id == snapshot.Team.Id);
            if (existing is null)
            {
                AddMobileTeamViewModel(snapshot.Team, snapshot.CurrentPatient);
            }
            else
            {
                existing.Apply(snapshot.Team, snapshot.CurrentPatient);
            }
        }
        SortMobileTeams();
        OnPropertyChanged(nameof(DeployedMobileTeams));
    }
    private async Task RefreshSummaryAsync()
    {
        AvailableStations = Stations.Count(station => !station.IsOccupied);
        OccupiedStations = Stations.Count(station => station.IsOccupied);
        PatientsSeenThisShift = await _treatmentCentreService.GetPatientsSeenThisShiftAsync();
        OnPropertyChanged(nameof(HasNoStations));
    }

    private async Task RefreshDashboardAsync()
    {
        var dashboard = await _treatmentCentreService.GetDashboardAsync();
        AvailableStations = dashboard.AvailableStations; OccupiedStations = dashboard.OccupiedStations; PatientsSeenThisShift = dashboard.PatientsSeen;
        OnPropertyChanged(nameof(TotalStations));
        AverageDischargeText = dashboard.AverageDischargeDuration is null ? "No discharges yet" : FormatDuration(dashboard.AverageDischargeDuration.Value);
        AverageThroughputText = dashboard.Throughput.Count == 0 ? "No discharges yet" : $"{dashboard.Throughput.Average(point => point.Discharges):0.0} per hour";
        HasComplaintBreakdown = dashboard.ComplaintBreakdown.Count > 0;
        HasDischargeRouteBreakdown = dashboard.DischargeRouteBreakdown.Count > 0;
        HasThroughput = dashboard.Throughput.Count > 0;
        HasDischargeDurations = dashboard.DischargeDurations.Count > 0;
        OnPropertyChanged(nameof(HasNoComplaintBreakdown)); OnPropertyChanged(nameof(HasNoDischargeRouteBreakdown)); OnPropertyChanged(nameof(HasNoThroughput)); OnPropertyChanged(nameof(HasNoDischargeDurations));
        ComplaintBreakdown.Clear(); foreach (var item in dashboard.ComplaintBreakdown.Select((item, index) => new DashboardChartSlice(item.Complaint, item.Count, ChartColors[index % ChartColors.Length]))) ComplaintBreakdown.Add(item);
        DischargeRouteBreakdown.Clear(); foreach (var item in dashboard.DischargeRouteBreakdown.Select((item, index) => new DashboardChartSlice(item.Route, item.Count, ChartColors[index % ChartColors.Length]))) DischargeRouteBreakdown.Add(item);
        ThroughputPoints.Clear(); foreach (var item in dashboard.Throughput) ThroughputPoints.Add(new DashboardChartPoint(item.BucketStart.LocalDateTime.ToString("HH:mm"), item.Discharges));
        DischargeDurationPoints.Clear(); foreach (var item in dashboard.DischargeDurations) DischargeDurationPoints.Add(new DashboardChartPoint(item.DischargedAt.LocalDateTime.ToString("HH:mm"), item.Duration.TotalMinutes));
        OccupancyPoints.Clear(); foreach (var item in dashboard.Occupancy) OccupancyPoints.Add(new DashboardChartPoint(item.ObservedAt.LocalDateTime.ToString("HH:mm"), item.OccupiedStations));
        CumulativeArrivalPoints.Clear(); foreach (var item in dashboard.CumulativeArrivals) CumulativeArrivalPoints.Add(new DashboardChartPoint(item.ObservedAt.LocalDateTime.ToString("HH:mm"), item.PatientsSeen));
    }

    private async Task RefreshPatientsAsync()
    {
        var stationNames = Stations.ToDictionary(station => station.Id, station => station.Name);
        var teamNames = MobileTeams.ToDictionary(team => team.Id, team => team.Callsign);
        var patients = await _treatmentCentreService.GetPatientsAsync();
        Patients.Clear();
        foreach (var patient in patients)
        {
            var currentLocation = patient.CurrentStationId is Guid stationId
                ? stationNames.GetValueOrDefault(stationId, "Unknown station")
                : patient.CurrentMobileTeamId is Guid teamId
                    ? teamNames.GetValueOrDefault(teamId, "Unknown mobile team")
                    : string.Empty;
            Patients.Add(new PatientViewModel(patient, currentLocation, DischargeRoutes, SavePatientAsync, RequestPatientDeletion) { IsEditMode = IsPatientEditMode });
        }
        OnPropertyChanged(nameof(HasNoPatients));
    }

    private void ClearPatientEdits()
    {
        if (!IsPatientEditMode) return;
        foreach (var patient in Patients) patient.CancelEdits();
        IsPatientEditMode = false;
    }

    private void Notify(string message, bool error = false)
    {
        BannerText = message; NotificationKind = error ? NotificationKind.Error : NotificationKind.Info; IsBannerVisible = true;
        _bannerTimer.Stop(); _bannerTimer.Interval = error ? TimeSpan.FromSeconds(8) : TimeSpan.FromSeconds(4); _bannerTimer.Start();
    }

    private void NotifyWarning(string message)
    {
        BannerText = message; NotificationKind = NotificationKind.Warning; IsBannerVisible = true;
        _bannerTimer.Stop(); _bannerTimer.Interval = TimeSpan.FromSeconds(4); _bannerTimer.Start();
    }

    private void RefreshClock()
    {
        CurrentTimeText = DateTimeOffset.Now.ToString("HH:mm:ss");
        foreach (var station in Stations) station.RefreshPatientArrivalText();
        foreach (var team in MobileTeams) team.RefreshPatientArrivalText();
    }
    private string UnlockPin => string.Concat(UnlockDigit1, UnlockDigit2, UnlockDigit3, UnlockDigit4, UnlockDigit5, UnlockDigit6);
    private void ClearUnlockPin() { UnlockDigit1 = ""; UnlockDigit2 = ""; UnlockDigit3 = ""; UnlockDigit4 = ""; UnlockDigit5 = ""; UnlockDigit6 = ""; }
    private static string FormatDuration(TimeSpan value) => value.TotalHours >= 1 ? $"{(int)value.TotalHours}h {value.Minutes}m" : $"{Math.Max(1, value.Minutes)}m";
    private static bool Intersects(StationViewModel first, StationViewModel second) => first.GridX < second.GridX + second.GridWidth && first.GridX + first.GridWidth > second.GridX && first.GridY < second.GridY + second.GridHeight && first.GridY + first.GridHeight > second.GridY;
    private static readonly string[] ChartColors = ["#87BBA2", "#55828B", "#3B6064", "#364958", "#C9E4CA"];
}

public sealed record StationDraft(string Name, string Type);
public sealed record MobileTeamDraft(string Callsign, string? Note);
public sealed record NewPatientDraft(string? PresentingComplaint);
public enum TcArea { Dashboard, Manager, Settings }
public enum TcPage { Map, Tables, Patients, Setup }
public enum SettingsPage { General, Operations, Displays }
public enum NotificationKind { Info, Warning, Error }
