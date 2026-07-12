using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TCMPlus.Domain.Models;

namespace TCMPlus.App.ViewModels;

public partial class StationViewModel : ViewModelBase
{

    private readonly Func<StationViewModel, Task> _saveStation;
    private readonly Func<StationViewModel, Task> _deleteStation;
    private readonly Action<StationViewModel> _requestPatient;
    private readonly Action<StationViewModel> _requestDischarge;
    private readonly Func<StationViewModel, StationGeometry, Task> _commitGeometry;
    private readonly Func<StationViewModel, Guid, Task> _dropPatient;

    public StationViewModel(
        Station station,
        Patient? currentPatient,
        Func<StationViewModel, Task> saveStation,
        Func<StationViewModel, Task> deleteStation,
        Action<StationViewModel> requestPatient,
        Action<StationViewModel> requestDischarge,
        Func<StationViewModel, StationGeometry, Task> commitGeometry,
        Func<StationViewModel, Guid, Task> dropPatient)
    {
        Id = station.Id;
        _name = station.Name;
        _type = station.Type;
        _gridX = station.GridX;
        _gridY = station.GridY;
        _gridWidth = station.GridWidth;
        _gridHeight = station.GridHeight;
        _currentPatient = currentPatient;
        _saveStation = saveStation;
        _deleteStation = deleteStation;
        _requestPatient = requestPatient;
        _requestDischarge = requestDischarge;
        _commitGeometry = commitGeometry;
        _dropPatient = dropPatient;
    }

    public Guid Id { get; }

    [ObservableProperty]
    private string _name = "";

    [ObservableProperty]
    private string _type = "";

    [ObservableProperty]
    private double _gridX;

    [ObservableProperty]
    private double _gridY;

    [ObservableProperty]
    private double _gridWidth;

    [ObservableProperty]
    private double _gridHeight;

    [ObservableProperty]
    private double _gridSizePixels = 24d;

    [ObservableProperty]
    private Patient? _currentPatient;

    [ObservableProperty]
    private bool _isEditMode;

    [ObservableProperty]
    private bool _isDropTarget;

    public double CanvasX => GridX * GridSizePixels;
    public double CanvasY => GridY * GridSizePixels;
    public double CanvasWidth => GridWidth * GridSizePixels;
    public double CanvasHeight => GridHeight * GridSizePixels;
    public bool IsOperationalMode => !IsEditMode;
    public bool IsOccupied => CurrentPatient is not null;
    public bool CanAddPatient => IsOperationalMode && !IsOccupied;
    public bool CanDischargePatient => IsOperationalMode && IsOccupied;
    public bool CanDelete => !IsOccupied;
    public string StatusText => IsOccupied ? "Occupied" : "Available";
    public string PatientArrivalText => CurrentPatient is null ? "" : FormatRelativeTime(CurrentPatient.AddedAt);
    public string PatientCounterText => CurrentPatient is null ? "" : $"Patient {CurrentPatient.PatientNumber}";

    public StationGeometry Geometry => new(GridX, GridY, GridWidth, GridHeight);

    public Station ToDomain() => new(Id, Name, Type, GridX, GridY, GridWidth, GridHeight);

    public void RestoreGeometry(StationGeometry geometry)
    {
        GridX = geometry.GridX;
        GridY = geometry.GridY;
        GridWidth = geometry.GridWidth;
        GridHeight = geometry.GridHeight;
    }

    public Task CommitGeometryAsync(StationGeometry originalGeometry) => _commitGeometry(this, originalGeometry);

    public void RefreshPatientArrivalText() => OnPropertyChanged(nameof(PatientArrivalText));

    [RelayCommand]
    private Task SaveStationAsync() => _saveStation(this);

    [RelayCommand]
    private Task DeleteStationAsync() => _deleteStation(this);

    [RelayCommand]
    private void AddPatient() => _requestPatient(this);

    [RelayCommand]
    private void DischargePatient() => _requestDischarge(this);

    public Task DropPatientAsync(Guid sourceStationId) => _dropPatient(this, sourceStationId);

    partial void OnCurrentPatientChanged(Patient? value)
    {
        OnPropertyChanged(nameof(IsOccupied));
        OnPropertyChanged(nameof(CanAddPatient));
        OnPropertyChanged(nameof(CanDischargePatient));
        OnPropertyChanged(nameof(CanDelete));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(PatientArrivalText));
        OnPropertyChanged(nameof(PatientCounterText));
    }

    partial void OnIsEditModeChanged(bool value)
    {
        OnPropertyChanged(nameof(IsOperationalMode));
        OnPropertyChanged(nameof(CanAddPatient));
        OnPropertyChanged(nameof(CanDischargePatient));
    }

    partial void OnGridXChanged(double value) => OnPropertyChanged(nameof(CanvasX));
    partial void OnGridYChanged(double value) => OnPropertyChanged(nameof(CanvasY));
    partial void OnGridWidthChanged(double value) => OnPropertyChanged(nameof(CanvasWidth));
    partial void OnGridHeightChanged(double value) => OnPropertyChanged(nameof(CanvasHeight));
    partial void OnGridSizePixelsChanged(double value) { OnPropertyChanged(nameof(CanvasX)); OnPropertyChanged(nameof(CanvasY)); OnPropertyChanged(nameof(CanvasWidth)); OnPropertyChanged(nameof(CanvasHeight)); }

    private static string FormatRelativeTime(DateTimeOffset addedAt)
    {
        var elapsed = DateTimeOffset.UtcNow - addedAt;
        if (elapsed < TimeSpan.FromMinutes(1))
        {
            return "now";
        }

        if (elapsed < TimeSpan.FromHours(1))
        {
            var minutes = Math.Max(1, (int)elapsed.TotalMinutes);
            return $"{minutes} minute{(minutes == 1 ? string.Empty : "s")} ago";
        }

        if (elapsed < TimeSpan.FromDays(1))
        {
            var hours = Math.Max(1, (int)elapsed.TotalHours);
            return $"{hours} hour{(hours == 1 ? string.Empty : "s")} ago";
        }

        var days = Math.Max(1, (int)elapsed.TotalDays);
        return $"{days} day{(days == 1 ? string.Empty : "s")} ago";
    }
}
