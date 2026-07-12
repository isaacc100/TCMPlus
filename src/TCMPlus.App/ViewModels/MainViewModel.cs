using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TCMPlus.Domain.Models;
using TCMPlus.Domain.Persistence;
using TCMPlus.Domain.Services;

namespace TCMPlus.App.ViewModels;

public partial class MainViewModel : ViewModelBase
{
    private readonly ITreatmentCentreService _treatmentCentreService;
    private readonly ITcSettingsRepository _settingsRepository;
    private readonly IShiftPinService _shiftPinService;
    private readonly IAppSettingsRepository _appSettingsRepository;
    private AppSettings? _appSettings;
    private TcSessionSettings? _sessionSettings;
    private readonly DispatcherTimer _clockTimer;
    private readonly DispatcherTimer _bannerTimer;

    public MainViewModel(ITreatmentCentreService treatmentCentreService, ITcSettingsRepository settingsRepository, IShiftPinService shiftPinService, IAppSettingsRepository appSettingsRepository, SessionDescriptor session)
    {
        _treatmentCentreService = treatmentCentreService;
        _settingsRepository = settingsRepository;
        _shiftPinService = shiftPinService;
        _appSettingsRepository = appSettingsRepository;
        Session = session;
        _shiftName = session.ShiftName;
        _clockTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _clockTimer.Tick += (_, _) => RefreshClock();
        _clockTimer.Start();
        _bannerTimer = new DispatcherTimer();
        _bannerTimer.Tick += (_, _) => { IsBannerVisible = false; _bannerTimer.Stop(); };
        RefreshClock();
    }

    public event EventHandler? AddStationRequested;
    public event Action<StationViewModel>? NewPatientRequested;
    public event Action<StationViewModel, StationViewModel>? PatientSwapConfirmationRequested;
    public event Action<StationViewModel>? DischargeRequested;
    public event EventHandler? SessionSwitchRequested;
    public event Action<ExternalDisplayMode>? ExternalDisplayRequested;
    public event EventHandler? SessionLockRequested;
    public event EventHandler? SessionUnlockRequested;

    public SessionDescriptor Session { get; }
    public ObservableCollection<StationViewModel> Stations { get; } = [];
    public ObservableCollection<DashboardEventViewModel> RecentActivity { get; } = [];
    public ObservableCollection<DashboardChartSlice> ComplaintBreakdown { get; } = [];
    public ObservableCollection<DashboardChartPoint> ThroughputPoints { get; } = [];
    public ObservableCollection<DashboardChartPoint> DischargeDurationPoints { get; } = [];
    public ObservableCollection<string> DischargeRoutes { get; } = [];
    public ObservableCollection<PatientViewModel> Patients { get; } = [];

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
    [ObservableProperty] private bool _hasThroughput;
    [ObservableProperty] private bool _hasDischargeDurations;

    public bool HasNoStations => Stations.Count == 0;
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
    public bool HasNoThroughput => !HasThroughput;
    public bool HasNoDischargeDurations => !HasDischargeDurations;
    public bool HasNoRecentActivity => RecentActivity.Count == 0;
    public string EditModeText => IsEditMode ? "Finish editing" : "Edit Treatment Centre";
    public string PatientEditModeText => IsPatientEditMode ? "Finish editing" : "Edit patients";
    public string MapStatusText => IsEditMode ? "Drag a station from anywhere except a corner. Use any corner to resize." : "Click an available station to add a patient. Drag a patient counter to transfer.";
    public double GridPixelSize => GridDensity switch { GridDensity.Standard => 20d, GridDensity.Dense => 16d, _ => 24d };

    public async Task InitializeAsync()
    {
        try
        {
            foreach (var item in await _treatmentCentreService.GetSnapshotAsync()) AddViewModel(item.Station, item.CurrentPatient);
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
        SelectedArea = TcArea.Manager;
        SelectedPage = TcPage.Patients;
        try { await RefreshPatientsAsync(); }
        catch (Exception exception) { Notify($"Unable to load patients: {exception.Message}", true); }
    }
    [RelayCommand] private void ShowSetup() { SelectedArea = TcArea.Manager; SelectedPage = TcPage.Setup; }
    [RelayCommand] private void ToggleEditMode() => IsEditMode = !IsEditMode;
    [RelayCommand] private void TogglePatientEditMode() => IsPatientEditMode = !IsPatientEditMode;
    [RelayCommand] private void RequestAddStation() => AddStationRequested?.Invoke(this, EventArgs.Empty);
    [RelayCommand] private void ShowSettings() { ClearPatientEdits(); SelectedArea = TcArea.Settings; }
    [RelayCommand] private void ShowSettingsGeneral() => SettingsPage = SettingsPage.General;
    [RelayCommand] private void ShowSettingsOperations() => SettingsPage = SettingsPage.Operations;
    [RelayCommand] private void ShowSettingsDisplays() => SettingsPage = SettingsPage.Displays;
    [RelayCommand] private async Task AddDischargeRouteAsync()
    {
        var route = NewDischargeRoute.Trim();
        if (string.IsNullOrWhiteSpace(route) || DischargeRoutes.Contains(route, StringComparer.OrdinalIgnoreCase)) return;
        DischargeRoutes.Add(route); NewDischargeRoute = ""; await SaveAppSettingsAsync(DischargeRoutes, (await _appSettingsRepository.GetAsync()).ExternalDisplayMode);
    }
    [RelayCommand] private async Task RemoveDischargeRouteAsync(string? route)
    {
        if (string.IsNullOrWhiteSpace(route) || DischargeRoutes.Count <= 1) return;
        DischargeRoutes.Remove(route); await SaveAppSettingsAsync(DischargeRoutes, (await _appSettingsRepository.GetAsync()).ExternalDisplayMode);
    }
    [RelayCommand] private void OpenExternalDisplay(string mode) => ExternalDisplayRequested?.Invoke(string.Equals(mode, "Map", StringComparison.OrdinalIgnoreCase) ? ExternalDisplayMode.Map : ExternalDisplayMode.Dashboard);
    [RelayCommand] private void RequestSessionSwitch() => SessionSwitchRequested?.Invoke(this, EventArgs.Empty);
    [RelayCommand] private async Task SaveQuickEntryAsync() => await SaveSessionOptionsAsync();
    [RelayCommand] private void SetCompactDensity() => SetGridDensity(GridDensity.Compact);
    [RelayCommand] private void SetStandardDensity() => SetGridDensity(GridDensity.Standard);
    [RelayCommand] private void SetDenseDensity() => SetGridDensity(GridDensity.Dense);
    [RelayCommand] private void BeginPinChange() => IsChangingPin = true;

    [RelayCommand]
    private void Lock() { ClearUnlockPin(); LockMessage = "Enter the shift PIN to continue."; SessionLockRequested?.Invoke(this, EventArgs.Empty); }

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

    partial void OnQuickEntryChanged(bool value) => _ = SaveSessionOptionsAsync();
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

    private void RequestNewPatient(StationViewModel station)
    {
        if (QuickEntry) { _ = SubmitNewPatientAsync(station, new NewPatientDraft(null)); return; }
        NewPatientRequested?.Invoke(station);
    }
    private void RequestDischarge(StationViewModel station)
    {
        if (QuickEntry) { _ = CompleteDischargeAsync(station, null); return; }
        DischargeRequested?.Invoke(station);
    }
    private async Task RequestPatientDropAsync(StationViewModel destination, Guid sourceStationId)
    {
        var source = Stations.FirstOrDefault(station => station.Id == sourceStationId);
        if (source is null || source == destination || !source.IsOccupied) return;
        if (destination.IsOccupied) { PatientSwapConfirmationRequested?.Invoke(source, destination); return; }
        try { await MovePatientAsync(source, destination, false); Notify($"{destination.PatientCounterText} moved to {destination.Name}."); }
        catch (Exception exception) { Notify(exception.Message, true); }
    }

    private async Task MovePatientAsync(StationViewModel source, StationViewModel destination, bool swap)
    {
        var result = await _treatmentCentreService.MovePatientAsync(source.Id, destination.Id, swap);
        source.CurrentPatient = result.SwappedPatient;
        destination.CurrentPatient = result.SourcePatient;
        await RefreshOperationalDataAsync();
    }

    private async Task SaveStationAsync(StationViewModel station)
    {
        try { await _treatmentCentreService.SaveStationAsync(station.ToDomain()); Notify($"{station.Name} saved."); }
        catch (Exception exception) { Notify(exception.Message, true); }
    }

    private async Task SavePatientAsync(PatientViewModel patient)
    {
        try
        {
            var updated = await _treatmentCentreService.UpdatePatientDetailsAsync(patient.Uid, patient.PresentingComplaint, patient.DischargeRoute);
            patient.AcceptSavedDetails(updated);
            await RefreshDashboardAsync();
            Notify($"Patient {patient.PatientNumber} saved.");
        }
        catch (Exception exception) { Notify(exception.Message, true); }
    }

    private async Task DeleteStationAsync(StationViewModel station)
    {
        try { await _treatmentCentreService.DeleteStationAsync(station.Id); Stations.Remove(station); await RefreshSummaryAsync(); OnPropertyChanged(nameof(HasNoStations)); Notify("Station deleted."); }
        catch (Exception exception) { Notify(exception.Message, true); }
    }

    public async Task CompleteDischargeAsync(StationViewModel station, string? route)
    {
        try { await _treatmentCentreService.DischargePatientAsync(station.Id, route); station.CurrentPatient = null; await RefreshOperationalDataAsync(); Notify($"{station.Name} is now available."); }
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
        AverageDischargeText = dashboard.AverageDischargeDuration is null ? "No discharges yet" : FormatDuration(dashboard.AverageDischargeDuration.Value);
        AverageThroughputText = dashboard.Throughput.Count == 0 ? "No discharges yet" : $"{dashboard.Throughput.Average(point => point.Discharges):0.0} per hour";
        RecentActivity.Clear(); foreach (var item in dashboard.RecentEvents) RecentActivity.Add(DashboardEventViewModel.FromEvent(item));
        HasComplaintBreakdown = dashboard.ComplaintBreakdown.Count > 0;
        HasThroughput = dashboard.Throughput.Count > 0;
        HasDischargeDurations = dashboard.DischargeDurations.Count > 0;
        OnPropertyChanged(nameof(HasNoComplaintBreakdown)); OnPropertyChanged(nameof(HasNoThroughput)); OnPropertyChanged(nameof(HasNoDischargeDurations)); OnPropertyChanged(nameof(HasNoRecentActivity));
        ComplaintBreakdown.Clear(); foreach (var item in dashboard.ComplaintBreakdown.Select((item, index) => new DashboardChartSlice(item.Complaint, item.Count, ChartColors[index % ChartColors.Length]))) ComplaintBreakdown.Add(item);
        ThroughputPoints.Clear(); foreach (var item in dashboard.Throughput) ThroughputPoints.Add(new DashboardChartPoint(item.BucketStart.LocalDateTime.ToString("HH:mm"), item.Discharges));
        DischargeDurationPoints.Clear(); foreach (var item in dashboard.DischargeDurations) DischargeDurationPoints.Add(new DashboardChartPoint(item.DischargedAt.LocalDateTime.ToString("HH:mm"), item.Duration.TotalMinutes));
    }

    private async Task RefreshPatientsAsync()
    {
        var stationNames = Stations.ToDictionary(station => station.Id, station => station.Name);
        var patients = await _treatmentCentreService.GetPatientsAsync();
        Patients.Clear();
        foreach (var patient in patients)
        {
            var stationName = patient.CurrentStationId is Guid stationId ? stationNames.GetValueOrDefault(stationId, "Unknown station") : string.Empty;
            Patients.Add(new PatientViewModel(patient, stationName, DischargeRoutes, SavePatientAsync) { IsEditMode = IsPatientEditMode });
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

    private void RefreshClock() { CurrentTimeText = DateTimeOffset.Now.ToString("HH:mm:ss"); foreach (var station in Stations) station.RefreshPatientArrivalText(); }
    private string UnlockPin => string.Concat(UnlockDigit1, UnlockDigit2, UnlockDigit3, UnlockDigit4, UnlockDigit5, UnlockDigit6);
    private void ClearUnlockPin() { UnlockDigit1 = ""; UnlockDigit2 = ""; UnlockDigit3 = ""; UnlockDigit4 = ""; UnlockDigit5 = ""; UnlockDigit6 = ""; }
    private static string FormatDuration(TimeSpan value) => value.TotalHours >= 1 ? $"{(int)value.TotalHours}h {value.Minutes}m" : $"{Math.Max(1, value.Minutes)}m";
    private static bool Intersects(StationViewModel first, StationViewModel second) => first.GridX < second.GridX + second.GridWidth && first.GridX + first.GridWidth > second.GridX && first.GridY < second.GridY + second.GridHeight && first.GridY + first.GridHeight > second.GridY;
    private static readonly string[] ChartColors = ["#87BBA2", "#55828B", "#3B6064", "#364958", "#C9E4CA"];
}

public sealed record StationDraft(string Name, string Type);
public sealed record NewPatientDraft(string? PresentingComplaint);
public enum TcArea { Dashboard, Manager, Settings }
public enum TcPage { Map, Tables, Patients, Setup }
public enum SettingsPage { General, Operations, Displays }
public enum NotificationKind { Info, Warning, Error }
