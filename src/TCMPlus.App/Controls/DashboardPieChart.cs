using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using TCMPlus.App.ViewModels;

namespace TCMPlus.App.Controls;

public sealed class DashboardPieChart : Control
{
    public static readonly StyledProperty<IEnumerable<DashboardChartSlice>?> SlicesProperty =
        AvaloniaProperty.Register<DashboardPieChart, IEnumerable<DashboardChartSlice>?>(nameof(Slices));

    public IEnumerable<DashboardChartSlice>? Slices
    {
        get => GetValue(SlicesProperty);
        set => SetValue(SlicesProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var slices = Slices?.Where(slice => slice.Value > 0).ToList() ?? [];
        if (slices.Count == 0) return;
        var total = slices.Sum(slice => slice.Value);
        var radius = Math.Max(8, Math.Min(Bounds.Width, Bounds.Height) / 2 - 8);
        var center = new Point(Bounds.Width / 2, Bounds.Height / 2);
        var start = -Math.PI / 2;
        foreach (var slice in slices)
        {
            var sweep = 2 * Math.PI * slice.Value / total;
            var end = start + sweep;
            var geometry = new StreamGeometry();
            using (var path = geometry.Open())
            {
                path.BeginFigure(center, true);
                path.LineTo(PointOnCircle(center, radius, start));
                path.ArcTo(PointOnCircle(center, radius, end), new Size(radius, radius), 0, sweep > Math.PI, SweepDirection.Clockwise, true);
                path.LineTo(center);
                path.EndFigure(true);
            }
            context.DrawGeometry(new SolidColorBrush(Color.Parse(slice.Color)), null, geometry);
            start = end;
        }
    }

    private static Point PointOnCircle(Point center, double radius, double angle) => new(center.X + radius * Math.Cos(angle), center.Y + radius * Math.Sin(angle));
}
