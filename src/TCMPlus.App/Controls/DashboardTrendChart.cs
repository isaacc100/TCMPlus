using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using TCMPlus.App.ViewModels;

namespace TCMPlus.App.Controls;

public sealed class DashboardTrendChart : Control
{
    public static readonly StyledProperty<IEnumerable<DashboardChartPoint>?> PointsProperty =
        AvaloniaProperty.Register<DashboardTrendChart, IEnumerable<DashboardChartPoint>?>(nameof(Points));
    public static readonly StyledProperty<IBrush?> LineBrushProperty =
        AvaloniaProperty.Register<DashboardTrendChart, IBrush?>(nameof(LineBrush), new SolidColorBrush(Color.Parse("#3B6064")));

    public IEnumerable<DashboardChartPoint>? Points { get => GetValue(PointsProperty); set => SetValue(PointsProperty, value); }
    public IBrush? LineBrush { get => GetValue(LineBrushProperty); set => SetValue(LineBrushProperty, value); }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var points = Points?.ToList() ?? [];
        if (points.Count == 0) return;
        const double padding = 18;
        var width = Math.Max(1, Bounds.Width - 2 * padding);
        var height = Math.Max(1, Bounds.Height - 2 * padding);
        var max = Math.Max(1, points.Max(point => point.Value));
        var pen = new Pen(LineBrush, 2.5);
        context.DrawLine(new Pen(new SolidColorBrush(Color.Parse("#D5E2DA"))), new Point(padding, padding + height), new Point(padding + width, padding + height));
        Point? previous = null;
        for (var index = 0; index < points.Count; index++)
        {
            var point = new Point(padding + (points.Count == 1 ? width / 2 : index * width / (points.Count - 1)), padding + height - points[index].Value / max * height);
            if (previous is not null) context.DrawLine(pen, previous.Value, point);
            context.DrawEllipse(LineBrush, null, point, 4, 4);
            previous = point;
        }
    }
}
