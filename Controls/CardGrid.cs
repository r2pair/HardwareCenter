using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using HardwareCenter.Core;

namespace HardwareCenter.Controls;

/// <summary>
/// Lays cards out in as many equal columns as fit, placing each card in the shortest column so
/// cards of different heights pack without gaps.
/// </summary>
public sealed class CardGrid : Panel
{
    public static readonly DependencyProperty MinColumnWidthProperty = DependencyProperty.Register(
        nameof(MinColumnWidth), typeof(double), typeof(CardGrid),
        new FrameworkPropertyMetadata(340.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty MaxColumnsProperty = DependencyProperty.Register(
        nameof(MaxColumns), typeof(int), typeof(CardGrid),
        new FrameworkPropertyMetadata(4, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty GapProperty = DependencyProperty.Register(
        nameof(Gap), typeof(double), typeof(CardGrid),
        new FrameworkPropertyMetadata(16.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public double MinColumnWidth
    {
        get => (double)GetValue(MinColumnWidthProperty);
        set => SetValue(MinColumnWidthProperty, value);
    }

    public int MaxColumns
    {
        get => (int)GetValue(MaxColumnsProperty);
        set => SetValue(MaxColumnsProperty, value);
    }

    public double Gap
    {
        get => (double)GetValue(GapProperty);
        set => SetValue(GapProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        double width = double.IsInfinity(availableSize.Width) ? MinColumnWidth : availableSize.Width;
        (int columns, double columnWidth) = Columns(width);
        double[] heights = new double[columns];

        foreach (UIElement child in InternalChildren)
        {
            if (child.Visibility == Visibility.Collapsed)
                continue;

            child.Measure(new Size(columnWidth, double.PositiveInfinity));
            heights[Shortest(heights)] += child.DesiredSize.Height + Gap;
        }

        return new Size(width, Math.Max(0, heights.Max() - Gap));
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        (int columns, double columnWidth) = Columns(finalSize.Width);
        double[] heights = new double[columns];

        foreach (UIElement child in InternalChildren)
        {
            if (child.Visibility == Visibility.Collapsed)
                continue;

            int column = Shortest(heights);
            child.Arrange(new Rect(column * (columnWidth + Gap), heights[column], columnWidth, child.DesiredSize.Height));
            heights[column] += child.DesiredSize.Height + Gap;
        }

        return finalSize;
    }

    private (int Columns, double ColumnWidth) Columns(double width)
    {
        int columns = Math.Clamp((int)((width + Gap) / (MinColumnWidth + Gap)), 1, Math.Max(1, MaxColumns));
        return (columns, Math.Max(0, (width - Gap * (columns - 1)) / columns));
    }

    private static int Shortest(double[] heights)
    {
        int best = 0;
        for (int i = 1; i < heights.Length; i++)
        {
            // The small tolerance keeps cards in reading order when columns are level.
            if (heights[i] < heights[best] - 0.5)
                best = i;
        }

        return best;
    }
}

/// <summary>Looks up a named icon path ("Cpu" → the "Icon.Cpu" resource).</summary>
public sealed class IconConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is string key ? Application.Current.TryFindResource("Icon." + key) as Geometry : null;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>True (or any non-null, non-empty value) → Visible. Pass "invert" as the parameter to flip it.</summary>
public sealed class VisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool visible = value switch
        {
            null => false,
            bool flag => flag,
            string text => text.Length > 0,
            int count => count > 0,
            _ => true,
        };

        if (parameter is string mode && mode.Equals("invert", StringComparison.OrdinalIgnoreCase))
            visible = !visible;

        return visible ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>A colour as a brush, optionally at reduced opacity given as the parameter ("0.16").</summary>
public sealed class ColorBrushConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not Color color)
            return null;

        double opacity = parameter is string text && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed) ? parsed : 1;
        var brush = new SolidColorBrush(color) { Opacity = opacity };
        brush.Freeze();
        return brush;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>"CPU Core #3 Thread #2" → "3.2", for the compact per-thread grid.</summary>
public sealed class ThreadLabelConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is string name ? ViewModels.Pick.ThreadLabel(name) : "";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>A load percentage as a whole number for tight spaces: 27.03 → "27%".</summary>
public sealed class WholePercentConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is float number ? number.ToString("F0", culture) + "%" : SensorFormat.Missing;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>
/// Lets its child fill the available width up to a limit and keeps it at the left edge beyond that,
/// so pages stay readable on very wide or maximized windows instead of stretching edge to edge.
/// </summary>
public sealed class WidthCap : Decorator
{
    public static readonly DependencyProperty MaxContentWidthProperty = DependencyProperty.Register(
        nameof(MaxContentWidth), typeof(double), typeof(WidthCap),
        new FrameworkPropertyMetadata(1500.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public double MaxContentWidth
    {
        get => (double)GetValue(MaxContentWidthProperty);
        set => SetValue(MaxContentWidthProperty, value);
    }

    protected override Size MeasureOverride(Size constraint)
    {
        if (Child == null)
            return default;

        Child.Measure(new Size(Math.Min(constraint.Width, MaxContentWidth), constraint.Height));
        return Child.DesiredSize;
    }

    protected override Size ArrangeOverride(Size arrangeSize)
    {
        Child?.Arrange(new Rect(0, 0, Math.Min(arrangeSize.Width, MaxContentWidth), arrangeSize.Height));
        return arrangeSize;
    }
}
