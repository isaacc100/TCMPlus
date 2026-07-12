using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using TCMPlus.App.ViewModels;
using TCMPlus.Domain.Models;

namespace TCMPlus.App.Controls;

public partial class StationCard : UserControl
{
    private static readonly DataFormat<string> PatientSourceStationFormat = DataFormat.CreateStringApplicationFormat("TCMPlus.PatientSourceStation");
    private const double MinimumGridWidth = 7d;
    private const double MinimumGridHeight = 7d;
    private Canvas? _canvas;
    private Point _pointerStart;
    private StationGeometry? _originalGeometry;
    private InteractionMode _interactionMode;
    private StationViewModel? _subscribedViewModel;

    public StationCard()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => SubscribeToViewModel();
    }

    private StationViewModel? ViewModel => DataContext as StationViewModel;

    private void SubscribeToViewModel()
    {
        if (_subscribedViewModel is not null)
        {
            _subscribedViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        }

        _subscribedViewModel = ViewModel;
        if (_subscribedViewModel is not null)
        {
            _subscribedViewModel.PropertyChanged += OnViewModelPropertyChanged;
        }

        UpdateCursor();
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(StationViewModel.IsEditMode))
        {
            UpdateCursor();
        }
    }

    private void UpdateCursor() => Cursor = ViewModel?.IsEditMode == true ? Cursor.Parse("SizeAll") : null;

    private void OnDragSurfacePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Source is Visual source && (source is Button || source.GetVisualAncestors().OfType<Button>().Any()))
        {
            return;
        }

        if (ViewModel is { IsOperationalMode: true, IsOccupied: false } viewModel && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            viewModel.AddPatientCommand.Execute(null);
            e.Handled = true;
            return;
        }

        StartInteraction(InteractionMode.Move, e);
    }

    private async void OnCounterPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (ViewModel is not { IsOperationalMode: true, IsOccupied: true } viewModel || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        var data = new DataTransfer();
        data.Add(DataTransferItem.Create(PatientSourceStationFormat, viewModel.Id.ToString("N")));
        await DragDrop.DoDragDropAsync(e, data, DragDropEffects.Move);
        e.Handled = true;
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        var sourceId = e.DataTransfer.TryGetValue(PatientSourceStationFormat);
        if (ViewModel is { IsOperationalMode: true } viewModel && sourceId is not null && sourceId != viewModel.Id.ToString("N"))
        {
            viewModel.IsDropTarget = true;
            e.DragEffects = DragDropEffects.Move;
            return;
        }

        e.DragEffects = DragDropEffects.None;
    }

    private void OnDragLeave(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is not null) ViewModel.IsDropTarget = false;
    }

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        if (ViewModel is not null) ViewModel.IsDropTarget = false;
        var sourceId = e.DataTransfer.TryGetValue(PatientSourceStationFormat);
        if (ViewModel is not { IsOperationalMode: true } viewModel || sourceId is null || !Guid.TryParse(sourceId, out var sourceStationId))
        {
            return;
        }

        await viewModel.DropPatientAsync(sourceStationId);
        e.Handled = true;
    }

    private void OnTopLeftResizePointerPressed(object? sender, PointerPressedEventArgs e) => StartInteraction(InteractionMode.ResizeTopLeft, e);
    private void OnTopRightResizePointerPressed(object? sender, PointerPressedEventArgs e) => StartInteraction(InteractionMode.ResizeTopRight, e);
    private void OnBottomLeftResizePointerPressed(object? sender, PointerPressedEventArgs e) => StartInteraction(InteractionMode.ResizeBottomLeft, e);
    private void OnBottomRightResizePointerPressed(object? sender, PointerPressedEventArgs e) => StartInteraction(InteractionMode.ResizeBottomRight, e);

    private void StartInteraction(InteractionMode mode, PointerPressedEventArgs e)
    {
        if (ViewModel is not { IsEditMode: true } viewModel || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        _canvas = this.GetVisualAncestors().OfType<Canvas>().FirstOrDefault();
        if (_canvas is null)
        {
            return;
        }

        _interactionMode = mode;
        _pointerStart = e.GetPosition(_canvas);
        _originalGeometry = viewModel.Geometry;
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_interactionMode == InteractionMode.None || _canvas is null || _originalGeometry is null || ViewModel is null)
        {
            return;
        }

        var position = e.GetPosition(_canvas);
        var deltaX = SnapToGridUnits(position.X - _pointerStart.X);
        var deltaY = SnapToGridUnits(position.Y - _pointerStart.Y);
        var original = _originalGeometry;
        var canvasColumns = _canvas.Bounds.Width / ViewModel.GridSizePixels;
        var canvasRows = _canvas.Bounds.Height / ViewModel.GridSizePixels;

        if (_interactionMode == InteractionMode.Move)
        {
            ViewModel.GridX = Clamp(original.GridX + deltaX, 0, canvasColumns - original.GridWidth);
            ViewModel.GridY = Clamp(original.GridY + deltaY, 0, canvasRows - original.GridHeight);
            return;
        }

        ApplyResize(ViewModel, original, deltaX, deltaY, canvasColumns, canvasRows);
    }

    private void ApplyResize(StationViewModel station, StationGeometry original, double deltaX, double deltaY, double columns, double rows)
    {
        switch (_interactionMode)
        {
            case InteractionMode.ResizeTopLeft:
                station.GridX = Clamp(original.GridX + deltaX, 0, original.GridX + original.GridWidth - MinimumGridWidth);
                station.GridY = Clamp(original.GridY + deltaY, 0, original.GridY + original.GridHeight - MinimumGridHeight);
                station.GridWidth = original.GridWidth + original.GridX - station.GridX;
                station.GridHeight = original.GridHeight + original.GridY - station.GridY;
                break;
            case InteractionMode.ResizeTopRight:
                station.GridY = Clamp(original.GridY + deltaY, 0, original.GridY + original.GridHeight - MinimumGridHeight);
                station.GridWidth = Clamp(original.GridWidth + deltaX, MinimumGridWidth, columns - original.GridX);
                station.GridHeight = original.GridHeight + original.GridY - station.GridY;
                break;
            case InteractionMode.ResizeBottomLeft:
                station.GridX = Clamp(original.GridX + deltaX, 0, original.GridX + original.GridWidth - MinimumGridWidth);
                station.GridWidth = original.GridWidth + original.GridX - station.GridX;
                station.GridHeight = Clamp(original.GridHeight + deltaY, MinimumGridHeight, rows - original.GridY);
                break;
            case InteractionMode.ResizeBottomRight:
                station.GridWidth = Clamp(original.GridWidth + deltaX, MinimumGridWidth, columns - original.GridX);
                station.GridHeight = Clamp(original.GridHeight + deltaY, MinimumGridHeight, rows - original.GridY);
                break;
        }
    }

    private async void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_interactionMode == InteractionMode.None || _originalGeometry is null || ViewModel is null)
        {
            return;
        }

        var original = _originalGeometry;
        ClearInteraction(e.Pointer);
        await ViewModel.CommitGeometryAsync(original);
        e.Handled = true;
    }

    private void OnPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        if (_originalGeometry is not null && ViewModel is not null)
        {
            ViewModel.RestoreGeometry(_originalGeometry);
        }

        ClearInteraction(null);
    }

    private void ClearInteraction(IPointer? pointer)
    {
        _canvas = null;
        _originalGeometry = null;
        _interactionMode = InteractionMode.None;
        pointer?.Capture(null);
    }

    private double SnapToGridUnits(double pixels) => Math.Round(pixels / (ViewModel?.GridSizePixels ?? 24d));
    private static double Clamp(double value, double min, double max) => Math.Min(Math.Max(value, min), Math.Max(min, max));

    private enum InteractionMode
    {
        None,
        Move,
        ResizeTopLeft,
        ResizeTopRight,
        ResizeBottomLeft,
        ResizeBottomRight
    }
}
