using System.Windows;
using System.Windows.Media;

namespace AIArena.Wpf.Controls;

public sealed class MetricSparklineControl : FrameworkElement
{
    public static readonly DependencyProperty ValuesProperty = DependencyProperty.Register(
        nameof(Values),
        typeof(IReadOnlyList<double>),
        typeof(MetricSparklineControl),
        new FrameworkPropertyMetadata(Array.Empty<double>(), FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty AccentBrushProperty = DependencyProperty.Register(
        nameof(AccentBrush),
        typeof(Brush),
        typeof(MetricSparklineControl),
        new FrameworkPropertyMetadata(Brushes.DeepSkyBlue, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ModeProperty = DependencyProperty.Register(
        nameof(Mode),
        typeof(string),
        typeof(MetricSparklineControl),
        new FrameworkPropertyMetadata("line", FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty MaxValueProperty = DependencyProperty.Register(
        nameof(MaxValue),
        typeof(double),
        typeof(MetricSparklineControl),
        new FrameworkPropertyMetadata(100d, FrameworkPropertyMetadataOptions.AffectsRender));

    public IReadOnlyList<double> Values
    {
        get => (IReadOnlyList<double>)GetValue(ValuesProperty);
        set => SetValue(ValuesProperty, value);
    }

    public Brush AccentBrush
    {
        get => (Brush)GetValue(AccentBrushProperty);
        set => SetValue(AccentBrushProperty, value);
    }

    public string Mode
    {
        get => (string)GetValue(ModeProperty);
        set => SetValue(ModeProperty, value);
    }

    public double MaxValue
    {
        get => (double)GetValue(MaxValueProperty);
        set => SetValue(MaxValueProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        return new Size(88, 34);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        var values = Values?.Where(value => !double.IsNaN(value) && !double.IsInfinity(value)).ToArray() ?? [];
        if (values.Length == 0 || ActualWidth <= 1 || ActualHeight <= 1)
        {
            return;
        }

        if (string.Equals(Mode, "bars", StringComparison.OrdinalIgnoreCase))
        {
            DrawBars(drawingContext, values);
        }
        else
        {
            DrawLine(drawingContext, values);
        }
    }

    private void DrawLine(DrawingContext drawingContext, IReadOnlyList<double> values)
    {
        if (values.Count == 1)
        {
            values = [values[0], values[0]];
        }

        if (values.All(value => Math.Abs(value) < 0.001))
        {
            DrawIdleLine(drawingContext);
            return;
        }

        var areaGeometry = new StreamGeometry();
        using (var context = areaGeometry.Open())
        {
            var first = PointFor(values[0], 0, values.Count);
            context.BeginFigure(new Point(first.X, ActualHeight), true, true);
            context.LineTo(first, true, false);
            for (var i = 1; i < values.Count; i++)
            {
                context.LineTo(PointFor(values[i], i, values.Count), true, false);
            }

            var last = PointFor(values[^1], values.Count - 1, values.Count);
            context.LineTo(new Point(last.X, ActualHeight), true, false);
        }

        areaGeometry.Freeze();

        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            for (var i = 0; i < values.Count; i++)
            {
                var point = PointFor(values[i], i, values.Count);
                if (i == 0)
                {
                    context.BeginFigure(point, false, false);
                }
                else
                {
                    context.LineTo(point, true, false);
                }
            }
        }

        geometry.Freeze();
        var accent = BrushColor(AccentBrush, Colors.DeepSkyBlue);
        drawingContext.DrawRoundedRectangle(
            new SolidColorBrush(Color.FromArgb(26, accent.R, accent.G, accent.B)),
            null,
            new Rect(0, 0, ActualWidth, ActualHeight),
            3,
            3);
        drawingContext.DrawGeometry(new SolidColorBrush(Color.FromArgb(48, accent.R, accent.G, accent.B)), null, areaGeometry);

        var glowPen = new Pen(new SolidColorBrush(Color.FromArgb(80, accent.R, accent.G, accent.B)), 5)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
            LineJoin = PenLineJoin.Round
        };
        var linePen = new Pen(AccentBrush, 2.35)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
            LineJoin = PenLineJoin.Round
        };
        drawingContext.DrawGeometry(null, glowPen, geometry);
        drawingContext.DrawGeometry(null, linePen, geometry);

        var lastPoint = PointFor(values[^1], values.Count - 1, values.Count);
        drawingContext.DrawEllipse(
            new SolidColorBrush(Color.FromArgb(54, accent.R, accent.G, accent.B)),
            null,
            lastPoint,
            5,
            5);
        drawingContext.DrawEllipse(AccentBrush, null, lastPoint, 2.4, 2.4);
    }

    private void DrawBars(DrawingContext drawingContext, IReadOnlyList<double> values)
    {
        var count = Math.Min(values.Count, 18);
        var start = values.Count - count;
        var gap = 2d;
        var width = Math.Max(2d, (ActualWidth - ((count - 1) * gap)) / count);
        var accent = BrushColor(AccentBrush, Colors.DeepSkyBlue);

        for (var i = 0; i < count; i++)
        {
            var normalized = Normalize(values[start + i]);
            var height = Math.Max(2d, normalized * ActualHeight);
            var x = i * (width + gap);
            var y = ActualHeight - height;
            var alpha = (byte)Math.Clamp(70 + (normalized * 185), 70, 255);
            var brush = new SolidColorBrush(Color.FromArgb(alpha, accent.R, accent.G, accent.B));
            drawingContext.DrawRoundedRectangle(
                new SolidColorBrush(Color.FromArgb(28, accent.R, accent.G, accent.B)),
                null,
                new Rect(x, 0, width, ActualHeight),
                1.4,
                1.4);
            drawingContext.DrawRoundedRectangle(
                brush,
                null,
                new Rect(x, y, width, height),
                1.4,
                1.4);
        }
    }

    private void DrawIdleLine(DrawingContext drawingContext)
    {
        var accent = BrushColor(AccentBrush, Colors.DeepSkyBlue);
        drawingContext.DrawRoundedRectangle(
            new SolidColorBrush(Color.FromArgb(24, accent.R, accent.G, accent.B)),
            null,
            new Rect(0, 0, ActualWidth, ActualHeight),
            3,
            3);

        var y = Math.Max(4, ActualHeight * 0.68);
        var baseline = new Pen(new SolidColorBrush(Color.FromArgb(110, accent.R, accent.G, accent.B)), 1.6)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round
        };
        drawingContext.DrawLine(baseline, new Point(2, y), new Point(Math.Max(2, ActualWidth - 2), y));

        var count = Math.Max(4, Math.Min(7, (int)Math.Floor(ActualWidth / 12)));
        var step = count <= 1 ? ActualWidth : (ActualWidth - 8) / (count - 1);
        for (var i = 0; i < count; i++)
        {
            var x = 4 + (i * step);
            var radius = i == count - 1 ? 2.2 : 1.45;
            var alpha = (byte)(i == count - 1 ? 190 : 95);
            drawingContext.DrawEllipse(
                new SolidColorBrush(Color.FromArgb(alpha, accent.R, accent.G, accent.B)),
                null,
                new Point(x, y),
                radius,
                radius);
        }
    }

    private Point PointFor(double value, int index, int count)
    {
        var x = count <= 1 ? ActualWidth : (ActualWidth / (count - 1)) * index;
        var y = ActualHeight - (Normalize(value) * ActualHeight);
        return new Point(x, Math.Clamp(y, 3, Math.Max(3, ActualHeight - 3)));
    }

    private double Normalize(double value)
    {
        var max = Math.Max(1, MaxValue);
        return Math.Clamp(value / max, 0, 1);
    }

    private static Color BrushColor(Brush brush, Color fallback)
    {
        return brush is SolidColorBrush solid ? solid.Color : fallback;
    }
}
