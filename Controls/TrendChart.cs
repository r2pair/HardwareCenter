using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using HardwareCenter.Core;
using HardwareCenter.ViewModels;

namespace HardwareCenter.Controls;

/// <summary>
/// History of one sensor (or two that share a unit) over a sliding time window. With
/// <see cref="ShowAxis"/> off it is a bare sparkline; with it on it adds a value axis, time labels,
/// an end marker and a hover read-out.
/// </summary>
public sealed class TrendChart : FrameworkElement
{
    public static readonly DependencyProperty SourceProperty = DependencyProperty.Register(
        nameof(Source), typeof(SensorVM), typeof(TrendChart),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, (d, _) => ((TrendChart)d).Attach()));

    public static readonly DependencyProperty Source2Property = DependencyProperty.Register(
        nameof(Source2), typeof(SensorVM), typeof(TrendChart),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, (d, _) => ((TrendChart)d).Attach()));

    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(
        nameof(Stroke), typeof(Color), typeof(TrendChart),
        new FrameworkPropertyMetadata(Color.FromRgb(0x39, 0x87, 0xE5), FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty Stroke2Property = DependencyProperty.Register(
        nameof(Stroke2), typeof(Color), typeof(TrendChart),
        new FrameworkPropertyMetadata(Color.FromRgb(0xD9, 0x59, 0x26), FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty WindowSecondsProperty = DependencyProperty.Register(
        nameof(WindowSeconds), typeof(double), typeof(TrendChart),
        new FrameworkPropertyMetadata(120.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty MinimumProperty = DependencyProperty.Register(
        nameof(Minimum), typeof(double), typeof(TrendChart),
        new FrameworkPropertyMetadata(double.NaN, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty MaximumProperty = DependencyProperty.Register(
        nameof(Maximum), typeof(double), typeof(TrendChart),
        new FrameworkPropertyMetadata(double.NaN, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ShowAxisProperty = DependencyProperty.Register(
        nameof(ShowAxis), typeof(bool), typeof(TrendChart),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    private static readonly Typeface LabelTypeface = new(new FontFamily("Segoe UI Variable Text, Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
    private static readonly Brush GridBrush = Resource("B.Grid", Color.FromArgb(0x12, 0xFF, 0xFF, 0xFF));
    private static readonly Brush LabelBrush = Resource("B.Text3", Color.FromRgb(0x7C, 0x84, 0x92));
    private static readonly Brush TextBrush = Resource("B.Text", Color.FromRgb(0xF1, 0xF3, 0xF7));
    private static readonly Brush SurfaceBrush = Resource("B.Card", Color.FromRgb(0x12, 0x15, 0x1B));
    private static readonly Brush TipBrush = Resource("B.Raised", Color.FromRgb(0x1D, 0x22, 0x2C));
    private static readonly Brush TipBorderBrush = Resource("B.BorderStrong", Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF));
    private const double LabelSize = 10.5;

    private SensorVM? _attachedA, _attachedB;
    private double? _hoverX;

    public TrendChart()
    {
        Loaded += (_, _) => Attach();
        Unloaded += (_, _) => Detach();
    }

    public SensorVM? Source
    {
        get => (SensorVM?)GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    /// <summary>Optional second series, drawn against the same axis.</summary>
    public SensorVM? Source2
    {
        get => (SensorVM?)GetValue(Source2Property);
        set => SetValue(Source2Property, value);
    }

    public Color Stroke
    {
        get => (Color)GetValue(StrokeProperty);
        set => SetValue(StrokeProperty, value);
    }

    public Color Stroke2
    {
        get => (Color)GetValue(Stroke2Property);
        set => SetValue(Stroke2Property, value);
    }

    /// <summary>How far back the chart reaches.</summary>
    public double WindowSeconds
    {
        get => (double)GetValue(WindowSecondsProperty);
        set => SetValue(WindowSecondsProperty, value);
    }

    /// <summary>Fixed bottom of the value axis, or NaN to choose one from the data.</summary>
    public double Minimum
    {
        get => (double)GetValue(MinimumProperty);
        set => SetValue(MinimumProperty, value);
    }

    /// <summary>Fixed top of the value axis, or NaN to choose one from the data.</summary>
    public double Maximum
    {
        get => (double)GetValue(MaximumProperty);
        set => SetValue(MaximumProperty, value);
    }

    public bool ShowAxis
    {
        get => (bool)GetValue(ShowAxisProperty);
        set => SetValue(ShowAxisProperty, value);
    }

    // ---- Subscriptions: sensors outlive charts, so a chart only listens while it is on screen ----

    private void Attach()
    {
        Detach();
        if (!IsLoaded)
            return;

        _attachedA = Source;
        _attachedB = Source2;
        if (_attachedA != null)
            _attachedA.Sampled += InvalidateVisual;
        if (_attachedB != null)
            _attachedB.Sampled += InvalidateVisual;
    }

    private void Detach()
    {
        if (_attachedA != null)
            _attachedA.Sampled -= InvalidateVisual;
        if (_attachedB != null)
            _attachedB.Sampled -= InvalidateVisual;

        _attachedA = _attachedB = null;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!ShowAxis)
            return;

        _hoverX = e.GetPosition(this).X;
        InvalidateVisual();
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        if (_hoverX != null)
        {
            _hoverX = null;
            InvalidateVisual();
        }
    }

    // ---- Drawing ----

    protected override void OnRender(DrawingContext dc)
    {
        double width = ActualWidth, height = ActualHeight;
        if (width < 8 || height < 8)
            return;

        // An invisible backdrop makes the whole area respond to the mouse, not just the line.
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, width, height));

        SensorVM? a = Source, b = Source2;
        SensorKind kind = (a ?? b)?.Kind ?? SensorKind.Load;
        long latest = Math.Max(a?.History.LatestTime ?? 0, b?.History.LatestTime ?? 0);
        long window = (long)(Math.Max(1, WindowSeconds) * TimeSpan.TicksPerSecond);
        long from = latest - window;
        double pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        (double low, double high) = latest == 0 ? (0, 1) : Range(a, b, from, kind);

        Rect plot = new(0, 2, width, height - 4);
        if (ShowAxis)
            plot = DrawAxis(dc, kind, low, high, width, height, pixelsPerDip);

        if (latest == 0 || plot.Width < 4 || plot.Height < 4)
            return;

        bool single = b == null;
        dc.PushClip(new RectangleGeometry(new Rect(plot.X, plot.Y - 2, plot.Width + 1, plot.Height + 4)));
        if (b != null)
            DrawSeries(dc, b, Stroke2, plot, from, window, low, high, fill: false);
        if (a != null)
            DrawSeries(dc, a, Stroke, plot, from, window, low, high, fill: single);
        dc.Pop();

        if (!ShowAxis)
            return;

        if (b != null)
            DrawEndMarker(dc, b, Stroke2, plot, from, window, low, high);
        if (a != null)
            DrawEndMarker(dc, a, Stroke, plot, from, window, low, high);

        if (_hoverX is double x && x >= plot.Left && x <= plot.Right)
            DrawHover(dc, a, b, kind, plot, from, window, low, high, x, pixelsPerDip);
    }

    private Rect DrawAxis(DrawingContext dc, SensorKind kind, double low, double high, double width, double height, double pixelsPerDip)
    {
        FormattedText[] labels =
        [
            Text(SensorFormat.Axis(kind, high), LabelBrush, pixelsPerDip),
            Text(SensorFormat.Axis(kind, (low + high) / 2), LabelBrush, pixelsPerDip),
            Text(SensorFormat.Axis(kind, low), LabelBrush, pixelsPerDip),
        ];

        double gutter = Math.Ceiling(labels.Max(l => l.Width)) + 8;
        const double timeBand = 18;
        var plot = new Rect(gutter, 7, Math.Max(0, width - gutter - 6), Math.Max(0, height - 7 - timeBand));

        var pen = new Pen(GridBrush, 1);
        pen.Freeze();

        for (int i = 0; i < 3; i++)
        {
            double y = Math.Round(plot.Top + plot.Height * i / 2) + 0.5;
            dc.DrawLine(pen, new Point(plot.Left, y), new Point(plot.Right, y));
            dc.DrawText(labels[i], new Point(gutter - 8 - labels[i].Width, y - labels[i].Height / 2));
        }

        FormattedText start = Text(DescribeWindow(WindowSeconds), LabelBrush, pixelsPerDip);
        FormattedText end = Text("now", LabelBrush, pixelsPerDip);
        dc.DrawText(start, new Point(plot.Left, plot.Bottom + 4));
        dc.DrawText(end, new Point(plot.Right - end.Width, plot.Bottom + 4));

        return plot;
    }

    private void DrawSeries(DrawingContext dc, SensorVM sensor, Color color, Rect plot, long from, long window, double low, double high, bool fill)
    {
        History history = sensor.History;
        int count = history.Count;
        if (count == 0)
            return;

        // Start one sample before the window so the line enters from the left edge.
        int first = 0;
        while (first < count - 1 && history.TimeAt(first + 1) < from)
            first++;

        var line = new StreamGeometry();
        StreamGeometry? area = fill ? new StreamGeometry() : null;

        using (StreamGeometryContext lineContext = line.Open())
        using (StreamGeometryContext? areaContext = area?.Open())
        {
            bool drawing = false;
            Point last = default;

            for (int i = first; i < count; i++)
            {
                float value = history.ValueAt(i);
                if (float.IsNaN(value))
                {
                    if (drawing)
                        CloseArea(areaContext, last, plot);

                    drawing = false;
                    continue;
                }

                var point = new Point(XOf(history.TimeAt(i), plot, from, window), YOf(value, plot, low, high));
                if (!drawing)
                {
                    lineContext.BeginFigure(point, false, false);
                    if (areaContext != null)
                    {
                        areaContext.BeginFigure(new Point(point.X, plot.Bottom), true, true);
                        areaContext.LineTo(point, false, false);
                    }

                    drawing = true;
                }
                else
                {
                    lineContext.LineTo(point, true, true);
                    areaContext?.LineTo(point, false, false);
                }

                last = point;
            }

            if (drawing)
                CloseArea(areaContext, last, plot);
        }

        if (area != null)
        {
            // A wash under the line: enough to give the series weight, never a solid block.
            var wash = new LinearGradientBrush(
                Color.FromArgb(0x38, color.R, color.G, color.B),
                Color.FromArgb(0x04, color.R, color.G, color.B),
                new Point(0, plot.Top), new Point(0, plot.Bottom))
            {
                MappingMode = BrushMappingMode.Absolute,
            };
            wash.Freeze();
            area.Freeze();
            dc.DrawGeometry(wash, null, area);
        }

        var pen = new Pen(new SolidColorBrush(color), ShowAxis ? 2 : 1.6)
        {
            LineJoin = PenLineJoin.Round,
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
        };
        pen.Freeze();
        line.Freeze();
        dc.DrawGeometry(null, pen, line);
    }

    private static void CloseArea(StreamGeometryContext? areaContext, Point last, Rect plot) =>
        areaContext?.LineTo(new Point(last.X, plot.Bottom), false, false);

    private static void DrawEndMarker(DrawingContext dc, SensorVM sensor, Color color, Rect plot, long from, long window, double low, double high)
    {
        History history = sensor.History;
        if (history.Count == 0)
            return;

        float value = history.ValueAt(history.Count - 1);
        if (float.IsNaN(value))
            return;

        var point = new Point(XOf(history.TimeAt(history.Count - 1), plot, from, window), YOf(value, plot, low, high));
        DrawDot(dc, point, color);
    }

    /// <summary>A series-coloured dot with a ring in the surface colour, so it stays legible on top of lines.</summary>
    private static void DrawDot(DrawingContext dc, Point point, Color color)
    {
        dc.DrawEllipse(SurfaceBrush, null, point, 6, 6);
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        dc.DrawEllipse(brush, null, point, 4, 4);
    }

    private void DrawHover(
        DrawingContext dc, SensorVM? a, SensorVM? b, SensorKind kind, Rect plot,
        long from, long window, double low, double high, double x, double pixelsPerDip)
    {
        long time = from + (long)((x - plot.Left) / plot.Width * window);
        (int indexA, long timeA) = Nearest(a?.History, time);
        (int indexB, long timeB) = Nearest(b?.History, time);
        if (indexA < 0 && indexB < 0)
            return;

        long sampleTime = indexA >= 0 ? timeA : timeB;
        double lineX = Math.Round(XOf(sampleTime, plot, from, window)) + 0.5;
        if (lineX < plot.Left - 1 || lineX > plot.Right + 1)
            return;

        var hairline = new Pen(TipBorderBrush, 1);
        hairline.Freeze();
        dc.DrawLine(hairline, new Point(lineX, plot.Top), new Point(lineX, plot.Bottom));

        var lines = new List<(Color? Swatch, FormattedText Text)>(3);
        AddHoverLine(dc, lines, a, indexA, Stroke, b != null, plot, low, high, lineX, pixelsPerDip);
        AddHoverLine(dc, lines, b, indexB, Stroke2, true, plot, low, high, lineX, pixelsPerDip);

        long latest = from + window;
        double secondsAgo = Math.Max(0, (latest - sampleTime) / (double)TimeSpan.TicksPerSecond);
        lines.Add((null, Text(secondsAgo < 1.5 ? "now" : $"{SensorFormat.Duration(secondsAgo)} ago", LabelBrush, pixelsPerDip)));

        // Read-out box beside the hairline, flipped to the left when it would run off the plot.
        const double padding = 8, swatch = 14;
        bool anySwatch = lines.Any(l => l.Swatch != null);
        double textWidth = lines.Max(l => l.Text.Width);
        double boxWidth = textWidth + padding * 2 + (anySwatch ? swatch : 0);
        double boxHeight = lines.Sum(l => l.Text.Height + 2) + padding * 2 - 2;
        double boxX = lineX + 10 + boxWidth > plot.Right ? lineX - 10 - boxWidth : lineX + 10;
        double boxY = plot.Top + 4;

        var border = new Pen(TipBorderBrush, 1);
        border.Freeze();
        dc.DrawRoundedRectangle(TipBrush, border, new Rect(Math.Round(boxX) + 0.5, Math.Round(boxY) + 0.5, Math.Round(boxWidth), Math.Round(boxHeight)), 6, 6);

        double y = boxY + padding;
        foreach ((Color? color, FormattedText text) in lines)
        {
            double textX = boxX + padding + (anySwatch ? swatch : 0);
            if (color is Color c)
            {
                var brush = new SolidColorBrush(c);
                brush.Freeze();
                dc.DrawEllipse(brush, null, new Point(boxX + padding + 4, y + text.Height / 2), 4, 4);
            }

            dc.DrawText(text, new Point(textX, y));
            y += text.Height + 2;
        }
    }

    private void AddHoverLine(
        DrawingContext dc, List<(Color? Swatch, FormattedText Text)> lines, SensorVM? sensor, int index, Color color,
        bool showSwatch, Rect plot, double low, double high, double lineX, double pixelsPerDip)
    {
        if (sensor == null || index < 0)
            return;

        float value = sensor.History.ValueAt(index);
        if (float.IsNaN(value))
            return;

        DrawDot(dc, new Point(lineX, YOf(value, plot, low, high)), color);
        lines.Add((showSwatch ? color : null, Text(SensorFormat.Format(sensor.Kind, value), TextBrush, pixelsPerDip, 12, FontWeights.SemiBold)));
    }

    // ---- Scales ----

    private static double XOf(long time, Rect plot, long from, long window) =>
        plot.Left + (double)(time - from) / window * plot.Width;

    private static double YOf(double value, Rect plot, double low, double high)
    {
        double t = high > low ? (value - low) / (high - low) : 0;
        return plot.Bottom - Math.Clamp(t, 0, 1) * plot.Height;
    }

    private (double Low, double High) Range(SensorVM? a, SensorVM? b, long from, SensorKind kind)
    {
        if (!double.IsNaN(Minimum) && !double.IsNaN(Maximum))
            return (Minimum, Maximum);

        double min = double.PositiveInfinity, max = double.NegativeInfinity;
        Scan(a?.History, from, ref min, ref max);
        Scan(b?.History, from, ref min, ref max);
        if (double.IsInfinity(min))
            return (double.IsNaN(Minimum) ? 0 : Minimum, double.IsNaN(Maximum) ? 1 : Maximum);

        double low, high;
        switch (kind)
        {
            // Quantities that hover around an operating point read best on a window around the data.
            case SensorKind.Temperature:
                low = Math.Floor((min - 2) / 5) * 5;
                high = Math.Ceiling((max + 2) / 5) * 5;
                if (high - low < 10)
                    high = low + 10;
                break;

            case SensorKind.Voltage:
                double pad = Math.Max((max - min) * 0.25, 0.01);
                low = Math.Max(0, min - pad);
                high = max + pad;
                break;

            case SensorKind.Clock:
                low = Math.Max(0, Math.Floor((min - 100) / 500) * 500);
                high = Math.Ceiling((max + 100) / 500) * 500;
                if (high - low < 500)
                    high = low + 500;
                break;

            // Everything else is a quantity where zero means "nothing", so the axis starts there.
            default:
                low = 0;
                high = NiceCeiling(kind, max * 1.1);
                break;
        }

        return (double.IsNaN(Minimum) ? low : Minimum, double.IsNaN(Maximum) ? high : Maximum);
    }

    private static void Scan(History? history, long from, ref double min, ref double max)
    {
        if (history == null)
            return;

        for (int i = history.Count - 1; i >= 0 && history.TimeAt(i) >= from; i--)
        {
            float value = history.ValueAt(i);
            if (float.IsNaN(value))
                continue;

            if (value < min)
                min = value;
            if (value > max)
                max = value;
        }
    }

    /// <summary>Rounds an axis top up to a 1-2-5 value, with a floor so idle noise does not fill the chart.</summary>
    private static double NiceCeiling(SensorKind kind, double value)
    {
        switch (kind)
        {
            case SensorKind.Load or SensorKind.Control or SensorKind.Level or SensorKind.Humidity:
                return 100;

            case SensorKind.Throughput:
                // Round within the unit the axis will be labelled in; past 500 of one unit, step to 1 of the next.
                value = Math.Max(value, 64 * 1024);
                double unit = Math.Pow(1024, Math.Floor(Math.Log(value, 1024)));
                double nice = OneTwoFive(value / unit);
                return (nice > 500 ? 1024 : nice) * unit;

            case SensorKind.Power:
                return OneTwoFive(Math.Max(value, 10));

            case SensorKind.Fan:
                return OneTwoFive(Math.Max(value, 500));

            default:
                return OneTwoFive(Math.Max(value, 1));
        }
    }

    private static double OneTwoFive(double value)
    {
        double magnitude = Math.Pow(10, Math.Floor(Math.Log10(value)));
        double fraction = value / magnitude;
        double nice = fraction <= 1 ? 1 : fraction <= 2 ? 2 : fraction <= 5 ? 5 : 10;
        return nice * magnitude;
    }

    private static (int Index, long Time) Nearest(History? history, long time)
    {
        if (history == null || history.Count == 0)
            return (-1, 0);

        int best = -1;
        long bestDistance = long.MaxValue;
        for (int i = history.Count - 1; i >= 0; i--)
        {
            long distance = Math.Abs(history.TimeAt(i) - time);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = i;
            }
            else if (history.TimeAt(i) < time)
            {
                break; // samples are in time order; it only gets further from here
            }
        }

        return (best, history.TimeAt(best));
    }

    private static string DescribeWindow(double seconds) =>
        seconds >= 120 ? $"{seconds / 60:0.#} min ago" : $"{seconds:0} s ago";

    private static FormattedText Text(string text, Brush brush, double pixelsPerDip, double size = LabelSize, FontWeight? weight = null)
    {
        Typeface typeface = weight is FontWeight w
            ? new Typeface(LabelTypeface.FontFamily, FontStyles.Normal, w, FontStretches.Normal)
            : LabelTypeface;

        return new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, size, brush, pixelsPerDip);
    }

    private static Brush Resource(string key, Color fallback)
    {
        if (Application.Current?.TryFindResource(key) is Brush brush)
            return brush;

        var solid = new SolidColorBrush(fallback);
        solid.Freeze();
        return solid;
    }
}
