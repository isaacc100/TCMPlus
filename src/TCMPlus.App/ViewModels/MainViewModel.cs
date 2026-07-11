using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Avalonia.Threading;
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

    public MainViewModel(
        ITreatmentCentreService treatmentCentreService,
        ITcSettingsRepository settingsRepository,
        IShiftPinService shiftPinService,
        SessionDescriptor session)
    {
        _treatmentCentreService = treatmentCentreService;
        _settingsRepository = settingsRepository;
        _shiftPinService = shiftPinService;
        Session = session;
        _shiftName = session.ShiftName;
        _clockTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _clockTimer.Tick += (_, _) => RefreshClock();
        RefreshClock();
        _clockTimer.Start();
    }

    public event EventHandler? AddStationRequested;

    public SessionDescriptor Session { get; }
    public ObservableCollection<StationViewModel> Stations { get; } = [];

    [ObservableProperty]
    private TcPage _selectedPage = TcPage.Map;

    [ObservableProperty]
    private bool _isEditMode;

    [ObservableProperty]
    private string _notice = "";

    [ObservableProperty]
    private string _newPin = "";

    [ObservableProperty]
    private string _shiftName = "";

    [ObservableProperty]
    private string _pinStatusText = "No shift PIN set.";

    [ObservableProperty]
    private int _availableStations;

    [ObservableProperty]
    private int _occupiedStations;

    [ObservableProperty]
    private int _patientsSeenThisShift;

    [ObservableProperty]
    private string _currentTimeText = "";

    [ObservableProperty]
    private bool _isLocked;

    [ObservableProperty]
    private string _unlockDigit1 = "";

    [ObservableProperty]
    private string _unlockDigit2 = "";

    [ObservableProperty]
    private string _unlockDigit3 = "";

    [ObservableProperty]
    private string _unlockDigit4 = "";

    [ObservableProperty]
    private string _unlockDigit5 = "";

    [ObservableProperty]
    private string _unlockDigit6 = "";

    [ObservableProperty]
    private string _lockMessage = "Enter the shift PIN to continue.";

    public bool HasNoStations => Stations.Count == 0;
    public bool IsMapPage => SelectedPage == TcPage.Map;
    public bool IsTablesPage => SelectedPage == TcPage.Tables;
    public bool IsSetupPage => SelectedPage == TcPage.Setup;
    public string EditModeText => IsEditMode ? "Finish editing" : "Edit Treatment Centre";
    public string MapStatusText => IsEditMode ? "Drag a station from anywhere except a corner. Use any corner to resize." : "Use the station controls to update occupancy.";

    public async Task InitializeAsync()
    {
        try
        {
            var snapshot = await _treatmentCentreService.GetSnapshotAsync();
            foreach (var item in snapshot)
            {
                AddViewModel(item.Station, item.CurrentPatient);
            }

            var settings = await _settingsRepository.GetAsync();
            ShiftName = string.IsNullOrWhiteSpace(settings.ShiftName) ? Session.ShiftName : settings.ShiftName;
            PinStatusText = settings.HasShiftPin ? "A shift PIN is stored for this session." : "No shift PIN set.";
            await RefreshSummaryAsync();
            Notice = Stations.Count == 0 ? "Edit the treatment centre to add the first station." : "";
        }
        catch (Exception exception)
        {
            Notice = $"Unable to load this session: {exception.Message}";
        }
    }

    [RelayCommand]
    private void ShowMap() => SelectedPage = TcPage.Map;

    [RelayCommand]
    private void ShowTables() => SelectedPage = TcPage.Tables;

    [RelayCommand]
    private void ShowSetup() => SelectedPage = TcPage.Setup;

    [RelayCommand]
    private void ToggleEditMode() => IsEditMode = !IsEditMode;

    [RelayCommand]
    private void RequestAddStation() => AddStationRequested?.Invoke(this, EventArgs.Empty);

    [RelayCommand]
    private void Lock()
    {
        ClearUnlockPin();
        LockMessage = "Enter the shift PIN to continue.";
        IsLocked = true;
    }

    [RelayCommand]
    private async Task UnlockAsync()
    {
        var settings = await _settingsRepository.GetAsync();
        if (_shiftPinService.Verify(UnlockPin, settings))
        {
            IsLocked = false;
            ClearUnlockPin();
            return;
        }

        LockMessage = "That PIN does not match this shift.";
        ClearUnlockPin();
    }

    [RelayCommand]
    private void ShowDashboardPlaceholder() => Notice = "Dashboard is coming in a future update.";

    [RelayCommand]
    private void ShowSettingsPlaceholder() => Notice = "Application settings are coming in a future update.";

    public async Task CreateStationAsync(StationDraft draft)
    {
        try
        {
            var station = await _treatmentCentreService.AddStationAsync(draft.Name, draft.Type);
            AddViewModel(station, null);
            await RefreshSummaryAsync();
            Notice = $"{station.Name} added.";
        }
        catch (Exception exception)
        {
            Notice = exception.Message;
        }
    }

    [RelayCommand]
    private async Task SaveShiftPinAsync()
    {
        if (string.IsNullOrWhiteSpace(ShiftName))
        {
            PinStatusText = "Enter a shift name.";
            return;
        }

        var settings = await _settingsRepository.GetAsync();
        if (!string.IsNullOrWhiteSpace(NewPin))
        {
            if (!_shiftPinService.IsValidFormat(NewPin))
            {
                PinStatusText = "Enter exactly six digits when changing the PIN.";
                return;
            }

            settings = _shiftPinService.CreateSettings(NewPin);
        }

        await _settingsRepository.SaveAsync(settings with { ShiftName = ShiftName.Trim() });
        NewPin = "";
        PinStatusText = "Shift details saved for this session.";
    }

    partial void OnSelectedPageChanged(TcPage value)
    {
        OnPropertyChanged(nameof(IsMapPage));
        OnPropertyChanged(nameof(IsTablesPage));
        OnPropertyChanged(nameof(IsSetupPage));
    }

    partial void OnIsEditModeChanged(bool value)
    {
        foreach (var station in Stations)
        {
            station.IsEditMode = value;
        }

        OnPropertyChanged(nameof(EditModeText));
        OnPropertyChanged(nameof(MapStatusText));
    }

    private void AddViewModel(Station station, Patient? patient)
    {
        var viewModel = new StationViewModel(
            station,
            patient,
            SaveStationAsync,
            DeleteStationAsync,
            AddPatientAsync,
            DischargePatientAsync,
            CommitGeometryAsync)
        {
            IsEditMode = IsEditMode
        };

        viewModel.PropertyChanged += async (_, args) =>
        {
            if (args.PropertyName is nameof(StationViewModel.CurrentPatient))
            {
                await RefreshSummaryAsync();
            }
        };

        Stations.Add(viewModel);
        OnPropertyChanged(nameof(HasNoStations));
    }

    private async Task SaveStationAsync(StationViewModel station)
    {
        try
        {
            await _treatmentCentreService.SaveStationAsync(station.ToDomain());
            Notice = $"{station.Name} saved.";
        }
        catch (Exception exception)
        {
            Notice = exception.Message;
        }
    }

    private async Task DeleteStationAsync(StationViewModel station)
    {
        try
        {
            await _treatmentCentreService.DeleteStationAsync(station.Id);
            Stations.Remove(station);
            await RefreshSummaryAsync();
            OnPropertyChanged(nameof(HasNoStations));
            Notice = "Station deleted.";
        }
        catch (Exception exception)
        {
            Notice = exception.Message;
        }
    }

    private async Task AddPatientAsync(StationViewModel station)
    {
        try
        {
            station.CurrentPatient = await _treatmentCentreService.AddPatientAsync(station.Id);
            Notice = $"{station.Name} is now occupied.";
        }
        catch (Exception exception)
        {
            Notice = exception.Message;
        }
    }

    private async Task DischargePatientAsync(StationViewModel station)
    {
        try
        {
            await _treatmentCentreService.DischargePatientAsync(station.Id);
            station.CurrentPatient = null;
            Notice = $"{station.Name} is now available.";
        }
        catch (Exception exception)
        {
            Notice = exception.Message;
        }
    }

    private async Task CommitGeometryAsync(StationViewModel station, StationGeometry originalGeometry)
    {
        if (Stations.Any(other => other != station && Intersects(station, other)))
        {
            station.RestoreGeometry(originalGeometry);
            Notice = "Stations cannot overlap. The previous position was restored.";
            return;
        }

        await SaveStationAsync(station);
    }

    private async Task RefreshSummaryAsync()
    {
        AvailableStations = Stations.Count(station => !station.IsOccupied);
        OccupiedStations = Stations.Count(station => station.IsOccupied);
        PatientsSeenThisShift = await _treatmentCentreService.GetPatientsSeenThisShiftAsync();
        OnPropertyChanged(nameof(HasNoStations));
    }

    private void RefreshClock()
    {
        CurrentTimeText = DateTimeOffset.Now.ToString("HH:mm:ss");
        foreach (var station in Stations)
        {
            station.RefreshPatientArrivalText();
        }
    }

    private string UnlockPin => string.Concat(UnlockDigit1, UnlockDigit2, UnlockDigit3, UnlockDigit4, UnlockDigit5, UnlockDigit6);

    private void ClearUnlockPin()
    {
        UnlockDigit1 = "";
        UnlockDigit2 = "";
        UnlockDigit3 = "";
        UnlockDigit4 = "";
        UnlockDigit5 = "";
        UnlockDigit6 = "";
    }

    private static bool Intersects(StationViewModel first, StationViewModel second) =>
        first.GridX < second.GridX + second.GridWidth &&
        first.GridX + first.GridWidth > second.GridX &&
        first.GridY < second.GridY + second.GridHeight &&
        first.GridY + first.GridHeight > second.GridY;
}

public sealed record StationDraft(string Name, string Type);

public enum TcPage
{
    Map,
    Tables,
    Setup
}
