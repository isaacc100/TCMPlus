using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TCMPlus.Domain.Models;

namespace TCMPlus.App.ViewModels;

public partial class PatientViewModel : ViewModelBase
{
    private const string EditTimeFormat = "dd/MM/yyyy HH:mm";
    private readonly Func<PatientViewModel, Task> _save;
    private readonly Action<PatientViewModel> _requestDelete;
    private string? _savedPresentingComplaint;
    private string? _savedDischargeRoute;
    private string? _savedDischargeOutcome;
    private string _savedAddedAtEditText;
    private string? _savedDischargedAtEditText;

    public PatientViewModel(
        Patient patient,
        string currentLocation,
        IEnumerable<string> configuredDischargeRoutes,
        Func<PatientViewModel, Task> save,
        Action<PatientViewModel> requestDelete)
    {
        Uid = patient.Uid;
        PatientNumber = patient.PatientNumber;
        AddedAt = patient.AddedAt;
        DischargedAt = patient.DischargedAt;
        CurrentLocation = currentLocation;
        _presentingComplaint = patient.PresentingComplaint;
        _dischargeRoute = patient.DischargeRoute;
        _dischargeOutcome = patient.DischargeOutcome;
        _addedAtEditText = FormatEditTime(patient.AddedAt);
        _dischargedAtEditText = patient.DischargedAt is null ? null : FormatEditTime(patient.DischargedAt.Value);
        _savedPresentingComplaint = patient.PresentingComplaint;
        _savedDischargeRoute = patient.DischargeRoute;
        _savedDischargeOutcome = patient.DischargeOutcome;
        _savedAddedAtEditText = _addedAtEditText;
        _savedDischargedAtEditText = _dischargedAtEditText;
        _save = save;
        _requestDelete = requestDelete;

        var routes = configuredDischargeRoutes.ToList();
        if (!string.IsNullOrWhiteSpace(patient.DischargeRoute) && !routes.Contains(patient.DischargeRoute, StringComparer.OrdinalIgnoreCase))
        {
            routes.Insert(0, patient.DischargeRoute);
        }
        routes.Insert(0, string.Empty);
        DischargeRoutes = routes;

        var outcomes = DischargeOutcomeOptions.Defaults.ToList();
        if (!string.IsNullOrWhiteSpace(patient.DischargeOutcome) && !outcomes.Contains(patient.DischargeOutcome, StringComparer.OrdinalIgnoreCase))
        {
            outcomes.Insert(0, patient.DischargeOutcome);
        }
        outcomes.Insert(0, string.Empty);
        DischargeOutcomes = outcomes;
    }

    public Guid Uid { get; }
    public int PatientNumber { get; }
    public DateTimeOffset AddedAt { get; private set; }
    public DateTimeOffset? DischargedAt { get; private set; }
    public string CurrentLocation { get; }
    public IReadOnlyList<string> DischargeRoutes { get; }
    public IReadOnlyList<string> DischargeOutcomes { get; }
    public bool IsDischarged => DischargedAt is not null;
    public string StatusText => IsDischarged ? "Discharged" : "Active";
    public string AddedAtText => AddedAt.LocalDateTime.ToString("dd MMM yyyy HH:mm");
    public string DischargedAtText => DischargedAt?.LocalDateTime.ToString("dd MMM yyyy HH:mm") ?? "—";
    public string CurrentLocationText => IsDischarged ? "—" : CurrentLocation;
    public string PresentingComplaintDisplay => string.IsNullOrWhiteSpace(PresentingComplaint) ? "—" : PresentingComplaint;
    public string DischargeRouteDisplay => string.IsNullOrWhiteSpace(DischargeRoute) ? "—" : DischargeRoute;
    public string DischargeOutcomeDisplay => string.IsNullOrWhiteSpace(DischargeOutcome) ? "—" : DischargeOutcome;
    public bool IsOperationalMode => !IsEditMode;
    public bool CanEditDischargeDetails => IsEditMode && IsDischarged;
    public bool ShowReadOnlyDischargeDetails => !CanEditDischargeDetails;

    [ObservableProperty] private string? _presentingComplaint;
    [ObservableProperty] private string? _dischargeRoute;
    [ObservableProperty] private string? _dischargeOutcome;
    [ObservableProperty] private string _addedAtEditText;
    [ObservableProperty] private string? _dischargedAtEditText;
    [ObservableProperty] private bool _isEditMode;
    [ObservableProperty] private bool _isSelected;

    public bool TryGetEditedTimes(out DateTimeOffset addedAt, out DateTimeOffset? dischargedAt, out string? error)
    {
        if (!TryParseLocalTime(AddedAtEditText, out addedAt))
        {
            dischargedAt = null;
            error = $"Enter the new time as {EditTimeFormat}.";
            return false;
        }

        dischargedAt = null;
        if (IsDischarged)
        {
            if (!TryParseLocalTime(DischargedAtEditText, out var parsedDischargedAt))
            {
                error = $"Enter the discharge time as {EditTimeFormat}.";
                return false;
            }
            dischargedAt = parsedDischargedAt;
        }

        if (dischargedAt is not null && dischargedAt <= addedAt)
        {
            error = "Discharge time must be after the patient's new time.";
            return false;
        }

        error = null;
        return true;
    }

    public void AcceptSavedDetails(Patient patient)
    {
        AddedAt = patient.AddedAt;
        DischargedAt = patient.DischargedAt;
        PresentingComplaint = patient.PresentingComplaint;
        DischargeRoute = patient.DischargeRoute;
        DischargeOutcome = patient.DischargeOutcome;
        AddedAtEditText = FormatEditTime(patient.AddedAt);
        DischargedAtEditText = patient.DischargedAt is null ? null : FormatEditTime(patient.DischargedAt.Value);
        _savedPresentingComplaint = patient.PresentingComplaint;
        _savedDischargeRoute = patient.DischargeRoute;
        _savedDischargeOutcome = patient.DischargeOutcome;
        _savedAddedAtEditText = AddedAtEditText;
        _savedDischargedAtEditText = DischargedAtEditText;
        OnPropertyChanged(nameof(AddedAtText));
        OnPropertyChanged(nameof(DischargedAtText));
    }

    public void AcceptBulkComplaint(string presentingComplaint)
    {
        PresentingComplaint = presentingComplaint;
        _savedPresentingComplaint = presentingComplaint;
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
        DischargeOutcome = _savedDischargeOutcome;
        AddedAtEditText = _savedAddedAtEditText;
        DischargedAtEditText = _savedDischargedAtEditText;
    }

    private static string FormatEditTime(DateTimeOffset value) => value.LocalDateTime.ToString(EditTimeFormat, CultureInfo.InvariantCulture);

    private static bool TryParseLocalTime(string? value, out DateTimeOffset result)
    {
        if (!DateTime.TryParseExact(value?.Trim(), EditTimeFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var localTime))
        {
            result = default;
            return false;
        }

        localTime = DateTime.SpecifyKind(localTime, DateTimeKind.Unspecified);
        result = new DateTimeOffset(localTime, TimeZoneInfo.Local.GetUtcOffset(localTime));
        return true;
    }

    partial void OnPresentingComplaintChanged(string? value) => OnPropertyChanged(nameof(PresentingComplaintDisplay));
    partial void OnDischargeRouteChanged(string? value) => OnPropertyChanged(nameof(DischargeRouteDisplay));
    partial void OnDischargeOutcomeChanged(string? value) => OnPropertyChanged(nameof(DischargeOutcomeDisplay));
    partial void OnIsEditModeChanged(bool value)
    {
        if (!value)
        {
            IsSelected = false;
        }
        OnPropertyChanged(nameof(IsOperationalMode));
        OnPropertyChanged(nameof(CanEditDischargeDetails));
        OnPropertyChanged(nameof(ShowReadOnlyDischargeDetails));
    }
}
