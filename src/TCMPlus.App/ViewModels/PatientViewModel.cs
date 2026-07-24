using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TCMPlus.Domain.Models;

namespace TCMPlus.App.ViewModels;

public partial class PatientViewModel : ViewModelBase
{
    private readonly Func<PatientViewModel, Task> _save;
    private readonly Action<PatientViewModel> _requestDelete;
    private string? _savedPresentingComplaint;
    private string? _savedDischargeRoute;

    public PatientViewModel(
        Patient patient,
        string stationName,
        IEnumerable<string> configuredDischargeRoutes,
        Func<PatientViewModel, Task> save,
        Action<PatientViewModel> requestDelete)
    {
        Uid = patient.Uid;
        PatientNumber = patient.PatientNumber;
        AddedAt = patient.AddedAt;
        DischargedAt = patient.DischargedAt;
        StationName = stationName;
        _presentingComplaint = patient.PresentingComplaint;
        _dischargeRoute = patient.DischargeRoute;
        _savedPresentingComplaint = patient.PresentingComplaint;
        _savedDischargeRoute = patient.DischargeRoute;
        _save = save;
        _requestDelete = requestDelete;

        var routes = configuredDischargeRoutes.ToList();
        if (!string.IsNullOrWhiteSpace(patient.DischargeRoute) && !routes.Contains(patient.DischargeRoute, StringComparer.OrdinalIgnoreCase)) routes.Insert(0, patient.DischargeRoute);
        routes.Insert(0, string.Empty);
        DischargeRoutes = routes;
    }

    public Guid Uid { get; }
    public int PatientNumber { get; }
    public DateTimeOffset AddedAt { get; }
    public DateTimeOffset? DischargedAt { get; }
    public string StationName { get; }
    public IReadOnlyList<string> DischargeRoutes { get; }
    public bool IsDischarged => DischargedAt is not null;
    public string StatusText => IsDischarged ? "Discharged" : "Active";
    public string AddedAtText => AddedAt.LocalDateTime.ToString("dd MMM yyyy HH:mm");
    public string DischargedAtText => DischargedAt?.LocalDateTime.ToString("dd MMM yyyy HH:mm") ?? "—";
    public string StationText => IsDischarged ? "—" : StationName;
    public string PresentingComplaintDisplay => string.IsNullOrWhiteSpace(PresentingComplaint) ? "—" : PresentingComplaint;
    public string DischargeRouteDisplay => string.IsNullOrWhiteSpace(DischargeRoute) ? "—" : DischargeRoute;
    public bool IsOperationalMode => !IsEditMode;
    public bool CanEditDischargeRoute => IsEditMode && IsDischarged;
    public bool ShowReadOnlyDischargeRoute => !CanEditDischargeRoute;

    [ObservableProperty] private string? _presentingComplaint;
    [ObservableProperty] private string? _dischargeRoute;
    [ObservableProperty] private bool _isEditMode;

    public void AcceptSavedDetails(Patient patient)
    {
        PresentingComplaint = patient.PresentingComplaint;
        DischargeRoute = patient.DischargeRoute;
        _savedPresentingComplaint = patient.PresentingComplaint;
        _savedDischargeRoute = patient.DischargeRoute;
    }

    [RelayCommand]
    private Task SaveAsync() => _save(this);

    [RelayCommand]
    private void Cancel() => CancelEdits();

    [RelayCommand]
    private void Delete() => _requestDelete(this);

    public void CancelEdits()
    {
        PresentingComplaint = _savedPresentingComplaint;
        DischargeRoute = _savedDischargeRoute;
    }

    partial void OnPresentingComplaintChanged(string? value) => OnPropertyChanged(nameof(PresentingComplaintDisplay));
    partial void OnDischargeRouteChanged(string? value) => OnPropertyChanged(nameof(DischargeRouteDisplay));
    partial void OnIsEditModeChanged(bool value)
    {
        OnPropertyChanged(nameof(IsOperationalMode));
        OnPropertyChanged(nameof(CanEditDischargeRoute));
        OnPropertyChanged(nameof(ShowReadOnlyDischargeRoute));
    }
}
