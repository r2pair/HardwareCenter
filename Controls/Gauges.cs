using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace HardwareCenter.Controls;

/// <summary>Shared behaviour for meters whose displayed value eases toward the real one.</summary>
public abstract class AnimatedMeter : FrameworkElement
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(AnimatedMeter),
        new FrameworkPropertyMetadata(0.0, (d, e) => ((AnimatedMeter)d).OnValueChanged((double)e.NewValue)));

    public static readonly DependencyProperty ColorProperty = DependencyProperty.Register(
        nameof(Color), typeof(Color), typeof(AnimatedMeter),
        new FrameworkPropertyMetadata(Colors.DodgerBlue, FrameworkPropertyMetadataOptions.AffectsRender));

    private static readonly DependencyProperty ShownValueProperty = DependencyProperty.Register(
        "ShownValue", typeof(double), typeof(AnimatedMeter),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Opacity of the unfilled track, drawn in the same hue as the fill.</summary>
    protected const double TrackOpacity = 0.17;

    /// <summary>
    /// Frames per second for the easing. Left alone, WPF redraws at the display's refresh rate, which on
    /// a 240 Hz monitor means hundreds of redraws per second for a meter that changes once a second.
    /// </summary>
    private const int AnimationFrameRate = 30;

    /// <summary>Changes smaller than this are applied directly; easing them is invisible and not free.</summary>
    private const double MinimumAnimatedChange = 0.004;

    /// <summary>Set false to apply values immediately everywhere (the "Animate gauges" setting).</summary>
    public static bool AnimationsEnabled { get; set; } = true;

    /// <summary>Fill level from 0 to 1.</summary>
    public double Value
    {
        get => (double)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public Color Color
    {
        get => (Color)GetValue(ColorProperty);
        set => SetValue(ColorProperty, value);
    }

    protected double ShownValue => Math.Clamp((double)GetValue(ShownValueProperty), 0, 1);

    protected virtual TimeSpan AnimationDuration => TimeSpan.FromMilliseconds(400);

    private void OnValueChanged(double value)
    {
        double target = double.IsNaN(value) ? 0 : Math.Clamp(value, 0, 1);
        double shown = (double)GetValue(ShownValueProperty);

        // Also honours "show animations in Windows" being switched off.
        if (!IsLoaded || !IsVisible || !AnimationsEnabled || !SystemParameters.ClientAreaAnimation
            || Math.Abs(target - shown) < MinimumAnimatedChange)
        {
            BeginAnimation(ShownValueProperty, null);
            SetValue(ShownValueProperty, target);
            return;
        }

        // Ease from what is on screen now (which may be mid-way through the previous ease).
        var animation = new DoubleAnimation(shown, target, AnimationDuration)
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.Stop,
        };
        Timeline.SetDesiredFrameRate(animation, AnimationFrameRate);

        // The target is stored as the plain value, so when the ease ends and its clock is released
        // the meter simply stays where it is and nothing keeps ticking.
        SetValue(ShownValueProperty, target);
        BeginAnimation(ShownValueProperty, animation, HandoffBehavior.SnapshotAndReplace);
    }

    protected static Pen RoundPen(Brush brush, double thickness)
    {
        var pen = new Pen(brush, thickness)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
            LineJoin = PenLineJoin.Round,
        };
        pen.Freeze();
        return pen;
    }

    protected static SolidColorBrush Solid(Color color, double opacity = 1)
    {
        var brush = new SolidColorBrush(color) { Opacity = opacity };
        brush.Freeze();
        return brush;
    }
}

/// <summary>A 270° arc meter, open at the bottom, sized to fit its bounds.</summary>
public sealed class ArcGauge : AnimatedMeter
{
    private const double StartDegrees = 135;
    private const double SweepDegrees = 270;

    public static readonly DependencyProperty ThicknessProperty = DependencyProperty.Register(
        nameof(Thickness), typeof(double), typeof(ArcGauge),
        new FrameworkPropertyMetadata(10.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public double Thickness
    {
        get => (double)GetValue(ThicknessProperty);
        set => SetValue(ThicknessProperty, value);
    }

    protected override TimeSpan AnimationDuration => TimeSpan.FromMilliseconds(550);

    protected override void OnRender(DrawingContext dc)
    {
        double thickness = Thickness;

        // The arc spans the full diameter horizontally, and from the top of the circle
        // down to sin(45°) below its centre vertically.
        double radius = Math.Min(
            (ActualWidth - thickness) / 2,
            (ActualHeight - thickness) / (1 + Math.Sin(Math.PI / 4)));
        if (radius <= 1)
            return;

        var centre = new Point(ActualWidth / 2, thickness / 2 + radius);
        Color color = Color;

        dc.DrawGeometry(null, RoundPen(Solid(color, TrackOpacity), thickness), Arc(centre, radius, SweepDegrees));

        double shown = ShownValue;
        if (shown <= 0.002)
            return;

        Geometry fill = Arc(centre, radius, SweepDegrees * shown);
        dc.DrawGeometry(null, RoundPen(Solid(color, 0.10), thickness + 7), fill); // soft halo
        dc.DrawGeometry(null, RoundPen(Solid(color), thickness), fill);
    }

    private static Geometry Arc(Point centre, double radius, double sweepDegrees)
    {
        Point start = PointAt(centre, radius, StartDegrees);
        Point end = PointAt(centre, radius, StartDegrees + sweepDegrees);

        var geometry = new StreamGeometry();
        using (StreamGeometryContext context = geometry.Open())
        {
            context.BeginFigure(start, false, false);
            context.ArcTo(end, new Size(radius, radius), 0, sweepDegrees > 180, SweepDirection.Clockwise, true, false);
        }

        geometry.Freeze();
        return geometry;
    }

    private static Point PointAt(Point centre, double radius, double degrees)
    {
        double radians = degrees * Math.PI / 180;
        return new Point(centre.X + radius * Math.Cos(radians), centre.Y + radius * Math.Sin(radians));
    }
}

/// <summary>A thin horizontal meter with rounded ends.</summary>
public sealed class MeterBar : AnimatedMeter
{
    static MeterBar()
    {
        HeightProperty.OverrideMetadata(typeof(MeterBar), new FrameworkPropertyMetadata(6.0));
    }

    protected override TimeSpan AnimationDuration => TimeSpan.FromMilliseconds(320);

    protected override Size MeasureOverride(Size availableSize) =>
        new(double.IsInfinity(availableSize.Width) ? 80 : 0, double.IsInfinity(availableSize.Height) ? 6 : Math.Min(availableSize.Height, 6));

    protected override void OnRender(DrawingContext dc)
    {
        double width = ActualWidth, height = ActualHeight;
        if (width <= 0 || height <= 0)
            return;

        double radius = height / 2;
        Color color = Color;
        dc.DrawRoundedRectangle(Solid(color, TrackOpacity), null, new Rect(0, 0, width, height), radius, radius);

        double shown = ShownValue;
        if (shown <= 0)
            return;

        // Anything above zero is at least a dot, so a small value is still visibly non-zero.
        double filled = Math.Max(height, width * shown);
        dc.DrawRoundedRectangle(Solid(color), null, new Rect(0, 0, Math.Min(filled, width), height), radius, radius);
    }
}

/// <summary>A line icon drawn from a 24 × 24 path, in the surrounding text colour.</summary>
public sealed class Icon : FrameworkElement
{
    public static readonly DependencyProperty DataProperty = DependencyProperty.Register(
        nameof(Data), typeof(Geometry), typeof(Icon),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ForegroundProperty = TextElement.ForegroundProperty.AddOwner(
        typeof(Icon),
        new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.Inherits));

    public static readonly DependencyProperty StrokeThicknessProperty = DependencyProperty.Register(
        nameof(StrokeThickness), typeof(double), typeof(Icon),
        new FrameworkPropertyMetadata(1.8, FrameworkPropertyMetadataOptions.AffectsRender));

    public Geometry? Data
    {
        get => (Geometry?)GetValue(DataProperty);
        set => SetValue(DataProperty, value);
    }

    public Brush Foreground
    {
        get => (Brush)GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    /// <summary>Line weight in the icon's own 24-unit coordinate space.</summary>
    public double StrokeThickness
    {
        get => (double)GetValue(StrokeThicknessProperty);
        set => SetValue(StrokeThicknessProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize) =>
        new(double.IsInfinity(availableSize.Width) ? 16 : 0, double.IsInfinity(availableSize.Height) ? 16 : 0);

    protected override void OnRender(DrawingContext dc)
    {
        Geometry? data = Data;
        double size = Math.Min(ActualWidth, ActualHeight);
        if (data == null || size <= 0)
            return;

        double scale = size / 24.0;
        var pen = new Pen(Foreground, StrokeThickness)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
            LineJoin = PenLineJoin.Round,
        };

        dc.PushTransform(new TranslateTransform((ActualWidth - size) / 2, (ActualHeight - size) / 2));
        dc.PushTransform(new ScaleTransform(scale, scale));
        dc.DrawGeometry(null, pen, data);
        dc.Pop();
        dc.Pop();
    }
}
