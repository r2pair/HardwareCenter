using System.Collections.ObjectModel;
using System.Windows.Media;
using HardwareCenter.Core;

namespace HardwareCenter.ViewModels;

/// <summary>Fixed-size ring of timestamped samples for one sensor. NaN marks a moment with no reading.</summary>
public sealed class History(int capacity = History.DefaultCapacity)
{
    public const int DefaultCapacity = 720;

    private readonly float[] _values = new float[capacity];
    private readonly long[] _times = new long[capacity];
    private int _head;

    public int Count { get; private set; }

    public long LatestTime => Count == 0 ? 0 : TimeAt(Count - 1);

    public void Add(long utcTicks, float value)
    {
        int slot = (_head + Count) % _values.Length;
        if (Count == _values.Length)
        {
            slot = _head;
            _head = (_head + 1) % _values.Length;
        }
        else
        {
            Count++;
        }

        _values[slot] = value;
        _times[slot] = utcTicks;
    }

    /// <summary>Sample by age order: 0 is the oldest retained sample.</summary>
    public float ValueAt(int index) => _values[(_head + index) % _values.Length];

    public long TimeAt(int index) => _times[(_head + index) % _times.Length];
}

public sealed class SensorVM : ObservableObject
{
    private readonly (float Warn, float Critical)? _thresholds;
    private string _valueText = SensorFormat.Missing;
    private string _compactText = SensorFormat.Missing;
    private string _minText = SensorFormat.Missing;
    private string _maxText = SensorFormat.Missing;
    private double _fraction;
    private Severity _severity;
    private Color _meterColor;

    internal SensorVM(HardwareVM owner, SensorReading reading)
    {
        Owner = owner;
        Key = reading.Key;
        Name = reading.Name;
        Kind = reading.Kind;
        Index = reading.Index;
        IsMinor = reading.IsMinor;
        IsLimit = Kind == SensorKind.Temperature && IsLimitName(Name);
        HasMeter = !IsLimit && !double.IsNaN(SensorFormat.Fraction(Kind, 0));
        _thresholds = Thresholds(owner.Kind, Kind, Name);
        _meterColor = owner.AccentColor;
    }

    public HardwareVM Owner { get; }
    public string Key { get; }
    public string Name { get; }
    public SensorKind Kind { get; }
    public int Index { get; }
    public bool IsMinor { get; }

    /// <summary>The reading has a natural 0..1 scale (live percentages and temperatures).</summary>
    public bool HasMeter { get; }

    /// <summary>A fixed threshold the device reports (e.g. an SSD's "Critical Temperature"), not a live reading.</summary>
    public bool IsLimit { get; }

    public History History { get; } = new();
    public float? Value { get; private set; }
    public float? Min { get; private set; }
    public float? Max { get; private set; }

    /// <summary>Raised after every sample, for charts that redraw themselves.</summary>
    public event Action? Sampled;

    private bool _stale;

    /// <summary>Recomputes the display values if samples arrived while nothing was listening.</summary>
    private void EnsureFresh()
    {
        if (_stale)
            Refresh();
    }

    public string ValueText { get { EnsureFresh(); return _valueText; } private set => Set(ref _valueText, value); }

    /// <summary>Rounded form for tight spaces: "27%" rather than "27.0 %".</summary>
    public string CompactText { get { EnsureFresh(); return _compactText; } private set => Set(ref _compactText, value); }

    public string MinText { get { EnsureFresh(); return _minText; } private set => Set(ref _minText, value); }
    public string MaxText { get { EnsureFresh(); return _maxText; } private set => Set(ref _maxText, value); }
    public double Fraction { get { EnsureFresh(); return _fraction; } private set => Set(ref _fraction, value); }
    public Color MeterColor { get { EnsureFresh(); return _meterColor; } private set => Set(ref _meterColor, value); }

    public Severity Severity
    {
        get { EnsureFresh(); return _severity; }
        private set
        {
            if (Set(ref _severity, value))
            {
                Raise(nameof(IsAlert));
                Raise(nameof(SeverityLabel));
            }
        }
    }

    public bool IsAlert => Severity != Severity.Normal;

    public string SeverityLabel => Severity switch
    {
        Severity.Critical => Kind == SensorKind.Temperature ? "Critical" : "Full",
        Severity.Warning => Kind == SensorKind.Temperature ? "Hot" : "High",
        _ => "",
    };

    internal int MissedTicks { get; set; }

    internal void Push(float? value, long utcTicks, bool quiet = false)
    {
        Value = value;
        History.Add(utcTicks, value ?? float.NaN);

        if (value is float v)
        {
            if (Min is not float min || v < min)
                Min = v;
            if (Max is not float max || v > max)
                Max = v;
        }

        // Formatting text for a sensor nobody is looking at is wasted work, and most sensors are off screen
        // at any moment. The text is brought up to date on demand instead (see EnsureFresh).
        if (quiet || !HasListeners)
        {
            _stale = true;
        }
        else
        {
            Refresh();
        }

        if (!quiet)
            Sampled?.Invoke();
    }

    /// <summary>The display text is out of date (unit change, or samples recorded while minimized).</summary>
    internal void Invalidate()
    {
        if (HasListeners)
            Refresh();
        else
            _stale = true;
    }

    internal void ResetRange()
    {
        Min = Value;
        Max = Value;
        Refresh();
    }

    /// <summary>Recomputes the display strings (after a new sample or a unit change).</summary>
    internal void Refresh()
    {
        _stale = false;
        ValueText = SensorFormat.Format(Kind, Value);
        CompactText = SensorFormat.FormatCompact(Kind, Value);
        MinText = SensorFormat.Format(Kind, Min);
        MaxText = SensorFormat.Format(Kind, Max);
        Fraction = Value is float v && HasMeter ? SensorFormat.Fraction(Kind, v) : 0;

        Severity severity = Severity.Normal;
        if (_thresholds is (float warn, float critical) && Value is float current)
            severity = current >= critical ? Severity.Critical : current >= warn ? Severity.Warning : Severity.Normal;

        Severity = severity;
        MeterColor = Palette.For(severity, Owner.AccentColor);
    }

    private static bool IsLimitName(string name) =>
        name.Contains("Warning", StringComparison.OrdinalIgnoreCase)
        || name.Contains("Critical", StringComparison.OrdinalIgnoreCase)
        || name.Contains("Distance", StringComparison.OrdinalIgnoreCase)
        || name.Contains("TjMax", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Where a reading starts to deserve attention. Deliberately conservative: a busy CPU at 80 °C or a
    /// GPU hot spot at 90 °C is normal, and a monitor that cries wolf gets ignored.
    /// </summary>
    private static (float Warn, float Critical)? Thresholds(HardwareKind owner, SensorKind kind, string name)
    {
        if (kind == SensorKind.Temperature)
        {
            if (IsLimitName(name))
                return null;

            bool innerSensor = name.Contains("Hot Spot", StringComparison.OrdinalIgnoreCase)
                               || name.Contains("Junction", StringComparison.OrdinalIgnoreCase);
            return owner switch
            {
                HardwareKind.Cpu => (85, 95),
                HardwareKind.Gpu => innerSensor ? (95, 105) : (84, 92),
                HardwareKind.Storage => (65, 75),
                HardwareKind.Memory => (65, 80),
                HardwareKind.Battery => (50, 60),
                _ => (80, 95),
            };
        }

        if (kind == SensorKind.Load)
        {
            if (owner == HardwareKind.Memory && name == "Memory")
                return (90, 97);
            if (owner == HardwareKind.Storage && name == "Used Space")
                return (90, 97);
        }

        return null;
    }
}

/// <summary>Sensors of one kind on one device, e.g. the "Temperatures" of a GPU.</summary>
public sealed class SensorGroupVM(string title, int order)
{
    public string Title { get; } = title;
    public int Order { get; } = order;
    public ObservableCollection<SensorVM> Sensors { get; } = [];
}

public sealed class HardwareVM : ObservableObject
{
    private const int RemoveAfterMissedTicks = 5;

    private readonly Dictionary<string, SensorVM> _byKey = new(StringComparer.Ordinal);
    private string _name;
    private DiskInfo? _disk;
    private NicInfo? _nic;

    internal HardwareVM(HardwareReading reading)
    {
        Key = reading.Key;
        ParentKey = reading.ParentKey;
        Kind = reading.Kind;
        Vendor = reading.Vendor;
        DiskNumber = reading.DiskNumber;
        NetworkId = reading.NetworkId;
        _name = reading.Name;

        AccentColor = Kind switch
        {
            HardwareKind.Cpu => Palette.Blue,
            HardwareKind.Gpu => Palette.Aqua,
            HardwareKind.Memory => Palette.Violet,
            HardwareKind.Storage => Palette.Magenta,
            _ => Palette.Slate,
        };

        IconKey = Kind switch
        {
            HardwareKind.Cpu => "Cpu",
            HardwareKind.Gpu => "Gpu",
            HardwareKind.Memory => "Memory",
            HardwareKind.Storage => "Storage",
            HardwareKind.Network => "Network",
            HardwareKind.Cooler => "Fan",
            HardwareKind.Psu or HardwareKind.Battery or HardwareKind.PowerMonitor => "Power",
            _ => "Board",
        };
    }

    public string Key { get; }
    public string? ParentKey { get; }
    public HardwareKind Kind { get; }
    public string? Vendor { get; }
    public int? DiskNumber { get; }
    public string? NetworkId { get; }
    public Color AccentColor { get; }
    public string IconKey { get; }

    public string Name { get => _name; private set => Set(ref _name, value); }

    /// <summary>Drive facts joined from Windows, for storage devices.</summary>
    public DiskInfo? Disk { get => _disk; internal set => Set(ref _disk, value); }

    /// <summary>Adapter facts joined from Windows, for network devices.</summary>
    public NicInfo? Nic { get => _nic; internal set => Set(ref _nic, value); }

    /// <summary>Every sensor that has produced a value, in display order.</summary>
    public ObservableCollection<SensorVM> Sensors { get; } = [];

    /// <summary>Summary-level sensors grouped by what they measure.</summary>
    public ObservableCollection<SensorGroupVM> Groups { get; } = [];

    /// <summary>Detail-level sensors (see <see cref="SensorReading.IsMinor"/>).</summary>
    public ObservableCollection<SensorVM> MinorSensors { get; } = [];

    /// <summary>Increments whenever a sensor is added or removed.</summary>
    public int Version { get; private set; }

    internal int MissedTicks { get; set; }

    public SensorVM? Find(SensorKind kind, params string[] names)
    {
        foreach (string name in names)
        {
            foreach (SensorVM sensor in Sensors)
            {
                if (sensor.Kind == kind && sensor.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                    return sensor;
            }
        }

        return null;
    }

    public SensorVM? First(SensorKind kind, Func<SensorVM, bool>? predicate = null)
    {
        foreach (SensorVM sensor in Sensors)
        {
            if (sensor.Kind == kind && !sensor.IsMinor && (predicate == null || predicate(sensor)))
                return sensor;
        }

        return null;
    }

    public IEnumerable<SensorVM> OfKind(SensorKind kind) => Sensors.Where(s => s.Kind == kind);

    /// <summary>Applies one reading. Returns true if sensors were added or removed.</summary>
    internal bool Apply(HardwareReading reading, long utcTicks, bool quiet = false)
    {
        bool changed = false;
        Name = reading.Name;

        foreach (SensorVM sensor in Sensors)
            sensor.MissedTicks++;

        foreach (SensorReading r in reading.Sensors)
        {
            if (!_byKey.TryGetValue(r.Key, out SensorVM? sensor))
            {
                // A sensor is listed once it has reported something; placeholders that never
                // produce a value on this machine would only add noise.
                if (r.Value == null)
                    continue;

                sensor = new SensorVM(this, r);
                _byKey.Add(r.Key, sensor);
                Insert(sensor);
                changed = true;
            }

            sensor.MissedTicks = 0;
            sensor.Push(r.Value, utcTicks, quiet);
        }

        for (int i = Sensors.Count - 1; i >= 0; i--)
        {
            SensorVM sensor = Sensors[i];
            if (sensor.MissedTicks == 0)
                continue;

            if (sensor.MissedTicks >= RemoveAfterMissedTicks)
            {
                Remove(sensor);
                changed = true;
            }
            else
            {
                sensor.Push(null, utcTicks, quiet);
            }
        }

        if (changed)
            Version++;

        return changed;
    }

    internal void RefreshFormatting()
    {
        foreach (SensorVM sensor in Sensors)
            sensor.Invalidate();
    }

    internal void ResetRanges()
    {
        foreach (SensorVM sensor in Sensors)
            sensor.ResetRange();
    }

    private static int Compare(SensorVM a, SensorVM b)
    {
        int order = SensorFormat.GroupOrder(a.Kind).CompareTo(SensorFormat.GroupOrder(b.Kind));
        if (order != 0)
            return order;

        order = a.Index.CompareTo(b.Index);
        return order != 0 ? order : string.CompareOrdinal(a.Name, b.Name);
    }

    private static void InsertSorted(ObservableCollection<SensorVM> list, SensorVM sensor)
    {
        int index = 0;
        while (index < list.Count && Compare(list[index], sensor) <= 0)
            index++;

        list.Insert(index, sensor);
    }

    private void Insert(SensorVM sensor)
    {
        InsertSorted(Sensors, sensor);

        if (sensor.IsMinor)
        {
            InsertSorted(MinorSensors, sensor);
            return;
        }

        string title = SensorFormat.GroupTitle(sensor.Kind);
        SensorGroupVM? group = Groups.FirstOrDefault(g => g.Title == title);
        if (group == null)
        {
            group = new SensorGroupVM(title, SensorFormat.GroupOrder(sensor.Kind));
            int index = 0;
            while (index < Groups.Count && Groups[index].Order <= group.Order)
                index++;

            Groups.Insert(index, group);
        }

        InsertSorted(group.Sensors, sensor);
    }

    private void Remove(SensorVM sensor)
    {
        _byKey.Remove(sensor.Key);
        Sensors.Remove(sensor);
        MinorSensors.Remove(sensor);

        foreach (SensorGroupVM group in Groups)
        {
            if (group.Sensors.Remove(sensor))
            {
                if (group.Sensors.Count == 0)
                    Groups.Remove(group);

                break;
            }
        }
    }
}

/// <summary>A labelled headline figure, e.g. "Temperature 54 °C", refreshed by its owner every update.</summary>
public sealed class StatVM(string label, bool hasMeter = false) : ObservableObject
{
    private string _text = SensorFormat.Missing;
    private string _unit = "";
    private string? _detail;
    private string? _hint;
    private double _fraction;
    private Color _color = Palette.Blue;
    private Severity _severity;

    public string Label { get; } = label;
    public bool HasMeter { get; } = hasMeter;

    public string Text { get => _text; private set => Set(ref _text, value); }
    public string Unit { get => _unit; private set => Set(ref _unit, value); }

    /// <summary>Optional secondary line, e.g. "595 GB free".</summary>
    public string? Detail { get => _detail; private set => Set(ref _detail, value); }

    /// <summary>Tooltip explaining why the value is unavailable.</summary>
    public string? Hint { get => _hint; private set => Set(ref _hint, value); }

    public double Fraction { get => _fraction; private set => Set(ref _fraction, value); }
    public Color Color { get => _color; private set => Set(ref _color, value); }

    public Severity Severity
    {
        get => _severity;
        private set
        {
            if (Set(ref _severity, value))
                Raise(nameof(IsAlert));
        }
    }

    public bool IsAlert => _severity != Severity.Normal;

    public void Show(string text, string unit, Color color, double fraction = 0, Severity severity = Severity.Normal, string? detail = null)
    {
        Text = text;
        Unit = unit;
        Fraction = double.IsNaN(fraction) ? 0 : fraction;
        Severity = severity;
        Color = Palette.For(severity, color);
        Detail = detail;
        Hint = null;
    }

    /// <summary>Shows a sensor's current value in headline (rounded) form.</summary>
    public void ShowSensor(SensorVM? sensor, string? missingHint = null, string? detail = null)
    {
        if (sensor?.Value is not float value)
        {
            ShowMissing(sensor == null ? missingHint : null);
            return;
        }

        (string number, string unit) = SensorFormat.SplitCompact(sensor.Kind, value);
        Show(number, unit, sensor.Owner.AccentColor, sensor.Fraction, sensor.Severity, detail);
    }

    public void ShowMissing(string? hint = null)
    {
        Text = SensorFormat.Missing;
        Unit = "";
        Fraction = 0;
        Severity = Severity.Normal;
        Detail = null;
        Hint = hint;
    }
}
