using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace TCMPlus.App.Controls;

public sealed class DotGridControl : Control
{
    public static readonly StyledProperty<double> SpacingProperty =
        AvaloniaProperty.Register<DotGridControl, double>(nameof(Spacing), 24d);

    public static readonly StyledProperty<double> DotRadiusProperty =
        AvaloniaProperty.Register<DotGridControl, double>(nameof(DotRadius), 1.1d);

    public double Spacing
    {
        get => GetValue(SpacingProperty);
        set => SetValue(SpacingProperty, value);
    }

    public double DotRadius
    {
        get => GetValue(DotRadiusProperty);
        set => SetValue(DotRadiusProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var brush = new SolidColorBrush(Color.Parse("#87BBA2"), 0.45);

        for (var x = 0d; x <= Bounds.Width; x += Spacing)
        {
            for (var y = 0d; y <= Bounds.Height; y += Spacing)
            {
                context.DrawEllipse(brush, null, new Point(x, y), DotRadius, DotRadius);
            }
        }
    }
}
