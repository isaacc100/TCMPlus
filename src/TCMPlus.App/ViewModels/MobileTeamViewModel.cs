using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TCMPlus.Domain.Models;

namespace TCMPlus.App.ViewModels;

public partial class MobileTeamViewModel : ViewModelBase
{
    private readonly Action<MobileTeamViewModel> _requestDeploy;
    private readonly Action<MobileTeamViewModel> _requestLocation;
    private readonly Action<MobileTeamViewModel> _requestPatient;
    private readonly Action<MobileTeamViewModel> _requestStandDown;
    private readonly Action<MobileTeamViewModel> _requestDischarge;
    private readonly Action<MobileTeamViewModel> _requestEdit;
    private readonly Action<MobileTeamViewModel> _requestDelete;
    private readonly Func<MobileTeamViewModel, Guid, Task> _dropPatient;

    public MobileTeamViewModel(
        MobileTeam team,
        Patient? currentPatient,
        Action<MobileTeamViewModel> requestDeploy,
        Action<MobileTeamViewModel> requestLocation,
        Action<MobileTeamViewModel> requestPatient,
        Action<MobileTeamViewModel> requestStandDown,
        Action<MobileTeamViewModel> requestDischarge,
        Action<MobileTeamViewModel> requestEdit,
        Action<MobileTeamViewModel> requestDelete,
        Func<MobileTeamViewModel, Guid, Task> dropPatient,
        bool allowDelete = true)
    {
        Id = team.Id;
        _callsign = team.Callsign;
        _note = team.Note;
        _isDeployed = team.IsDeployed;
        _deploymentLocation = team.DeploymentLocation;
        _currentPatient = currentPatient;
        _requestDeploy = requestDeploy;
        _requestLocation = requestLocation;
        _requestPatient = requestPatient;
        _requestStandDown = requestStandDown;
        _requestDischarge = requestDischarge;
        _requestEdit = requestEdit;
        _requestDelete = requestDelete;
        _dropPatient = dropPatient;
        AllowDelete = allowDelete;
    }

    public Guid Id { get; }
    public bool AllowDelete { get; }
    [ObservableProperty] private string _callsign;
    [ObservableProperty] private string? _note;
    [ObservableProperty] private bool _isDeployed;
    [ObservableProperty] private string? _deploymentLocation;
    [ObservableProperty] private Patient? _currentPatient;
    [ObservableProperty] private bool _isDropTarget;

    public bool IsAvailable => !IsDeployed;
    public bool IsOccupied => CurrentPatient is not null;
    public bool CanAddPatient => !IsOccupied;
    public bool CanAcceptPatientDrop => IsDeployed && !IsOccupied;
    public bool HasNote => !string.IsNullOrWhiteSpace(Note);
    public bool HasLocation => !string.IsNullOrWhiteSpace(DeploymentLocation);
    public bool CanDelete => AllowDelete && !IsDeployed && !IsOccupied;
    public string StatusText => IsDeployed ? "Deployed" : "Available";
    public string NoteText => HasNote ? Note! : "No note";
    public string LocationText => HasLocation ? DeploymentLocation! : "Location not set";
    public string PatientCounterText => CurrentPatient is null ? "" : $"Patient {CurrentPatient.PatientNumber}";
    public string PatientArrivalText => CurrentPatient is null ? "" : FormatRelativeTime(CurrentPatient.AddedAt);

    public MobileTeam ToDomain() => new(Id, Callsign, Note, IsDeployed, DeploymentLocation);

    public void Apply(MobileTeam team, Patient? currentPatient)
    {
        Callsign = team.Callsign;
        Note = team.Note;
        IsDeployed = team.IsDeployed;
        DeploymentLocation = team.DeploymentLocation;
        CurrentPatient = currentPatient;
    }

    public Task DropPatientAsync(Guid patientUid) => _dropPatient(this, patientUid);
    public void RefreshPatientArrivalText() => OnPropertyChanged(nameof(PatientArrivalText));

    [RelayCommand] private void Deploy() => _requestDeploy(this);
    [RelayCommand] private void EditLocation() => _requestLocation(this);
    [RelayCommand] private void AddPatient() => _requestPatient(this);
    [RelayCommand] private void StandDown() => _requestStandDown(this);
    [RelayCommand] private void DischargePatient() => _requestDischarge(this);
    [RelayCommand] private void Edit() => _requestEdit(this);
    [RelayCommand] private void Delete() => _requestDelete(this);

    partial void OnIsDeployedChanged(bool value)
    {
        OnPropertyChanged(nameof(IsAvailable));
        OnPropertyChanged(nameof(CanAcceptPatientDrop));
        OnPropertyChanged(nameof(CanDelete));
        OnPropertyChanged(nameof(StatusText));
    }

    partial void OnDeploymentLocationChanged(string? value)
    {
        OnPropertyChanged(nameof(HasLocation));
        OnPropertyChanged(nameof(LocationText));
    }

    partial void OnNoteChanged(string? value)
    {
        OnPropertyChanged(nameof(HasNote));
        OnPropertyChanged(nameof(NoteText));
    }

    partial void OnCurrentPatientChanged(Patient? value)
    {
        OnPropertyChanged(nameof(IsOccupied));
        OnPropertyChanged(nameof(CanAddPatient));
        OnPropertyChanged(nameof(CanAcceptPatientDrop));
        OnPropertyChanged(nameof(CanDelete));
        OnPropertyChanged(nameof(PatientCounterText));
        OnPropertyChanged(nameof(PatientArrivalText));
    }

    private static string FormatRelativeTime(DateTimeOffset addedAt)
    {
        var elapsed = DateTimeOffset.UtcNow - addedAt;
        if (elapsed < TimeSpan.FromMinutes(1)) return "now";
        if (elapsed < TimeSpan.FromHours(1))
        {
            var minutes = Math.Max(1, (int)elapsed.TotalMinutes);
            return $"{minutes} minute{(minutes == 1 ? "" : "s")} ago";
        }
        if (elapsed < TimeSpan.FromDays(1))
        {
            var hours = Math.Max(1, (int)elapsed.TotalHours);
            return $"{hours} hour{(hours == 1 ? "" : "s")} ago";
        }
        var days = Math.Max(1, (int)elapsed.TotalDays);
        return $"{days} day{(days == 1 ? "" : "s")} ago";
    }
}
