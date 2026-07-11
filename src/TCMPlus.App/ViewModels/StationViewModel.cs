using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TCMPlus.Domain.Models;

namespace TCMPlus.App.ViewModels;

public partial class StationViewModel : ViewModelBase
{
    public const double GridSizePixels = 24d;

    private readonly Func<StationViewModel, Task> _saveStation;
    private readonly Func<StationViewModel, Task> _deleteStation;
    private readonly Func<StationViewModel, Task> _addPatient;
    private readonly Func<StationViewModel, Task> _dischargePatient;
    private readonly Func<StationViewModel, StationGeometry, Task> _commitGeometry;

    public StationViewModel(
        Station station,
        Patient? currentPatient,
        Func<StationViewModel, Task> saveStation,
        Func<StationViewModel, Task> deleteStation,
        Func<StationViewModel, Task> addPatient,
        Func<StationViewModel, Task> dischargePatient,
        Func<StationViewModel, StationGeometry, Task> commitGeometry)
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
        _addPatient = addPatient;
        _dischargePatient = dischargePatient;
        _commitGeometry = commitGeometry;
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
    private Patient? _currentPatient;

    [ObservableProperty]
    private bool _isEditMode;

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
    public string PatientAddedText => CurrentPatient?.AddedAt.ToLocalTime().ToString("dd MMM HH:mm") ?? "";

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

    [RelayCommand]
    private Task SaveStationAsync() => _saveStation(this);

    [RelayCommand]
    private Task DeleteStationAsync() => _deleteStation(this);

    [RelayCommand]
    private Task AddPatientAsync() => _addPatient(this);

    [RelayCommand]
    private Task DischargePatientAsync() => _dischargePatient(this);

    partial void OnCurrentPatientChanged(Patient? value)
    {
        OnPropertyChanged(nameof(IsOccupied));
        OnPropertyChanged(nameof(CanAddPatient));
        OnPropertyChanged(nameof(CanDischargePatient));
        OnPropertyChanged(nameof(CanDelete));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(PatientAddedText));
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
}
