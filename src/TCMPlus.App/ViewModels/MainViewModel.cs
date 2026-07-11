using System.Collections.ObjectModel;
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
    private string _pinStatusText = "No shift PIN set.";

    [ObservableProperty]
    private int _availableStations;

    [ObservableProperty]
    private int _occupiedStations;

    [ObservableProperty]
    private int _patientsSeenThisShift;

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
        if (!_shiftPinService.IsValidFormat(NewPin))
        {
            PinStatusText = "Enter exactly six digits.";
            return;
        }

        await _settingsRepository.SaveAsync(_shiftPinService.CreateSettings(NewPin));
        NewPin = "";
        PinStatusText = "Shift PIN saved for this session.";
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
