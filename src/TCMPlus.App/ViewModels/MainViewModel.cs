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
    private readonly DispatcherTimer _clockTimer;
    private readonly DispatcherTimer _bannerTimer;

    public MainViewModel(ITreatmentCentreService treatmentCentreService, ITcSettingsRepository settingsRepository, IShiftPinService shiftPinService, SessionDescriptor session)
    {
        _treatmentCentreService = treatmentCentreService;
        _settingsRepository = settingsRepository;
        _shiftPinService = shiftPinService;
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

    public SessionDescriptor Session { get; }
    public ObservableCollection<StationViewModel> Stations { get; } = [];
    public ObservableCollection<DashboardEventViewModel> RecentActivity { get; } = [];
    public ObservableCollection<DashboardChartSlice> ComplaintBreakdown { get; } = [];
    public ObservableCollection<DashboardChartPoint> ThroughputPoints { get; } = [];
    public ObservableCollection<DashboardChartPoint> DischargeDurationPoints { get; } = [];

    [ObservableProperty] private TcArea _selectedArea = TcArea.Manager;
    [ObservableProperty] private TcPage _selectedPage = TcPage.Map;
    [ObservableProperty] private bool _isEditMode;
    [ObservableProperty] private string _newPin = "";
    [ObservableProperty] private string _shiftName = "";
    [ObservableProperty] private string _pinStatusText = "No shift PIN set.";
    [ObservableProperty] private int _availableStations;
    [ObservableProperty] private int _occupiedStations;
    [ObservableProperty] private int _patientsSeenThisShift;
    [ObservableProperty] private string _currentTimeText = "";
    [ObservableProperty] private bool _isLocked;
    [ObservableProperty] private string _unlockDigit1 = "";
    [ObservableProperty] private string _unlockDigit2 = "";
    [ObservableProperty] private string _unlockDigit3 = "";
    [ObservableProperty] private string _unlockDigit4 = "";
    [ObservableProperty] private string _unlockDigit5 = "";
    [ObservableProperty] private string _unlockDigit6 = "";
    [ObservableProperty] private string _lockMessage = "Enter the shift PIN to continue.";
    [ObservableProperty] private bool _isBannerVisible;
    [ObservableProperty] private string _bannerText = "";
    [ObservableProperty] private bool _isBannerError;
    [ObservableProperty] private string _averageDischargeText = "No discharges yet";
    [ObservableProperty] private string _averageThroughputText = "No discharges yet";
    [ObservableProperty] private bool _hasComplaintBreakdown;
    [ObservableProperty] private bool _hasThroughput;
    [ObservableProperty] private bool _hasDischargeDurations;

    public bool HasNoStations => Stations.Count == 0;
    public bool IsDashboard => SelectedArea == TcArea.Dashboard;
    public bool IsManager => SelectedArea == TcArea.Manager;
    public bool IsMapPage => IsManager && SelectedPage == TcPage.Map;
    public bool IsTablesPage => IsManager && SelectedPage == TcPage.Tables;
    public bool IsSetupPage => IsManager && SelectedPage == TcPage.Setup;
    public bool HasNoComplaintBreakdown => !HasComplaintBreakdown;
    public bool HasNoThroughput => !HasThroughput;
    public bool HasNoDischargeDurations => !HasDischargeDurations;
    public bool HasNoRecentActivity => RecentActivity.Count == 0;
    public string EditModeText => IsEditMode ? "Finish editing" : "Edit Treatment Centre";
    public string MapStatusText => IsEditMode ? "Drag a station from anywhere except a corner. Use any corner to resize." : "Click an available station to add a patient. Drag a patient counter to transfer.";

    public async Task InitializeAsync()
    {
        try
        {
            foreach (var item in await _treatmentCentreService.GetSnapshotAsync()) AddViewModel(item.Station, item.CurrentPatient);
            var settings = await _settingsRepository.GetAsync();
            ShiftName = string.IsNullOrWhiteSpace(settings.ShiftName) ? Session.ShiftName : settings.ShiftName;
            PinStatusText = settings.HasShiftPin ? "A shift PIN is stored for this session." : "No shift PIN set.";
            await RefreshSummaryAsync();
            await RefreshDashboardAsync();
            if (Stations.Count == 0) Notify("Edit the treatment centre to add the first station.");
        }
        catch (Exception exception) { Notify($"Unable to load this session: {exception.Message}", true); }
    }

    [RelayCommand] private async Task ShowDashboardAsync() { SelectedArea = TcArea.Dashboard; await RefreshDashboardAsync(); }
    [RelayCommand] private void ShowManager() => SelectedArea = TcArea.Manager;
    [RelayCommand] private void ShowMap() { SelectedArea = TcArea.Manager; SelectedPage = TcPage.Map; }
    [RelayCommand] private void ShowTables() { SelectedArea = TcArea.Manager; SelectedPage = TcPage.Tables; }
    [RelayCommand] private void ShowSetup() { SelectedArea = TcArea.Manager; SelectedPage = TcPage.Setup; }
    [RelayCommand] private void ToggleEditMode() => IsEditMode = !IsEditMode;
    [RelayCommand] private void RequestAddStation() => AddStationRequested?.Invoke(this, EventArgs.Empty);
    [RelayCommand] private void ShowSettingsPlaceholder() => Notify("Application settings are coming in a future update.");

    [RelayCommand]
    private void Lock() { ClearUnlockPin(); LockMessage = "Enter the shift PIN to continue."; IsLocked = true; }

    [RelayCommand]
    private async Task UnlockAsync()
    {
        var settings = await _settingsRepository.GetAsync();
        if (_shiftPinService.Verify(UnlockPin, settings)) { IsLocked = false; ClearUnlockPin(); return; }
        LockMessage = "That PIN does not match this shift."; ClearUnlockPin();
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
        await _settingsRepository.SaveAsync(settings with { ShiftName = ShiftName.Trim() });
        NewPin = ""; PinStatusText = "Shift details saved for this session.";
    }

    partial void OnSelectedAreaChanged(TcArea value) => RefreshAreaProperties();
    partial void OnSelectedPageChanged(TcPage value) => RefreshAreaProperties();
    partial void OnIsEditModeChanged(bool value)
    {
        foreach (var station in Stations) station.IsEditMode = value;
        OnPropertyChanged(nameof(EditModeText)); OnPropertyChanged(nameof(MapStatusText));
    }

    private void RefreshAreaProperties()
    {
        OnPropertyChanged(nameof(IsDashboard)); OnPropertyChanged(nameof(IsManager)); OnPropertyChanged(nameof(IsMapPage)); OnPropertyChanged(nameof(IsTablesPage)); OnPropertyChanged(nameof(IsSetupPage));
    }

    private void AddViewModel(Station station, Patient? patient)
    {
        var viewModel = new StationViewModel(station, patient, SaveStationAsync, DeleteStationAsync, RequestNewPatient, DischargePatientAsync, CommitGeometryAsync, RequestPatientDropAsync) { IsEditMode = IsEditMode };
        viewModel.PropertyChanged += async (_, args) => { if (args.PropertyName is nameof(StationViewModel.CurrentPatient)) await RefreshSummaryAsync(); };
        Stations.Add(viewModel); OnPropertyChanged(nameof(HasNoStations));
    }

    private void RequestNewPatient(StationViewModel station) => NewPatientRequested?.Invoke(station);
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

    private async Task DeleteStationAsync(StationViewModel station)
    {
        try { await _treatmentCentreService.DeleteStationAsync(station.Id); Stations.Remove(station); await RefreshSummaryAsync(); OnPropertyChanged(nameof(HasNoStations)); Notify("Station deleted."); }
        catch (Exception exception) { Notify(exception.Message, true); }
    }

    private async Task DischargePatientAsync(StationViewModel station)
    {
        try { await _treatmentCentreService.DischargePatientAsync(station.Id); station.CurrentPatient = null; await RefreshOperationalDataAsync(); Notify($"{station.Name} is now available."); }
        catch (Exception exception) { Notify(exception.Message, true); }
    }

    private async Task CommitGeometryAsync(StationViewModel station, StationGeometry originalGeometry)
    {
        if (Stations.Any(other => other != station && Intersects(station, other))) { station.RestoreGeometry(originalGeometry); Notify("Stations cannot overlap. The previous position was restored.", true); return; }
        await SaveStationAsync(station);
    }

    private async Task RefreshOperationalDataAsync() { await RefreshSummaryAsync(); await RefreshDashboardAsync(); }
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

    private void Notify(string message, bool error = false)
    {
        BannerText = message; IsBannerError = error; IsBannerVisible = true;
        _bannerTimer.Stop(); _bannerTimer.Interval = error ? TimeSpan.FromSeconds(8) : TimeSpan.FromSeconds(4); _bannerTimer.Start();
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
public enum TcArea { Dashboard, Manager }
public enum TcPage { Map, Tables, Setup }
