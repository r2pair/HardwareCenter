using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows.Media;
using HardwareCenter.Core;

namespace HardwareCenter.ViewModels;

/// <summary>What every page needs to know about the machine besides the sensors themselves.</summary>
public sealed class MonitorContext(AppSettings settings)
{
    public AppSettings Settings { get; } = settings;
    public AccessInfo Access { get; set; } = new(false, false, null, false);
    public Inventory? Inventory { get; set; }
    public IReadOnlyList<HardwareVM> Hardware { get; set; } = [];

    /// <summary>Why a sensor that should exist is not there.</summary>
    public string MissingHint => Access.Level switch
    {
        AccessLevel.Limited => "Needs administrator rights",
        AccessLevel.DriverMissing => "Needs the PawnIO sensor driver",
        AccessLevel.DriverUnavailable => "The sensor driver is not responding",
        _ => "Not reported by this device",
    };
}

/// <summary>Chooses the sensors that matter most on each kind of device, by the names the sensor library uses.</summary>
internal static partial class Pick
{
    public static SensorVM? CpuLoad(HardwareVM cpu) =>
        cpu.Find(SensorKind.Load, "CPU Total") ?? cpu.First(SensorKind.Load);

    public static SensorVM? CpuTemperature(HardwareVM cpu) =>
        cpu.Find(SensorKind.Temperature, "Core (Tctl/Tdie)", "CPU Package", "Core (Tdie)", "Core (Tctl)", "Core Max", "Core Average")
        ?? cpu.First(SensorKind.Temperature, s => !s.IsLimit);

    public static SensorVM? CpuPower(HardwareVM cpu) =>
        cpu.Find(SensorKind.Power, "Package", "CPU Package") ?? cpu.First(SensorKind.Power);

    /// <summary>Average core speed in MHz: the library's own average if it has one, else the mean of the per-core clocks.</summary>
    public static float? CpuSpeed(HardwareVM cpu)
    {
        SensorVM? average = cpu.Find(SensorKind.Clock, "Cores (Average)", "Average Effective Speed");
        if (average?.Value is float value && value > 0)
            return value;

        float sum = 0;
        int count = 0;
        foreach (SensorVM clock in cpu.OfKind(SensorKind.Clock))
        {
            if (clock.Value is float mhz && mhz > 0
                && clock.Name.Contains("Core #", StringComparison.Ordinal)
                && !clock.Name.Contains("Effective", StringComparison.Ordinal))
            {
                sum += mhz;
                count++;
            }
        }

        return count > 0 ? sum / count : null;
    }

    public static List<SensorVM> CpuThreads(HardwareVM cpu) =>
        cpu.OfKind(SensorKind.Load).Where(s => ThreadName().IsMatch(s.Name)).ToList();

    /// <summary>"CPU Core #3 Thread #2" → "3.2"; "CPU Core #3" → "3".</summary>
    public static string ThreadLabel(string sensorName)
    {
        Match match = ThreadName().Match(sensorName);
        if (!match.Success)
            return sensorName;

        return match.Groups[2].Success ? $"{match.Groups[1].Value}.{match.Groups[2].Value}" : match.Groups[1].Value;
    }

    public static SensorVM? GpuLoad(HardwareVM gpu) => gpu.Find(SensorKind.Load, "GPU Core", "D3D 3D");

    public static SensorVM? GpuTemperature(HardwareVM gpu) =>
        gpu.Find(SensorKind.Temperature, "GPU Core") ?? gpu.First(SensorKind.Temperature, s => !s.IsLimit);

    public static SensorVM? GpuPower(HardwareVM gpu) =>
        gpu.Find(SensorKind.Power, "GPU Package", "GPU Power", "GPU Core") ?? gpu.First(SensorKind.Power);

    public static SensorVM? GpuFan(HardwareVM gpu) => gpu.First(SensorKind.Fan);

    public static (SensorVM? Used, SensorVM? Total) GpuMemory(HardwareVM gpu)
    {
        SensorVM? used = gpu.Find(SensorKind.SmallData, "GPU Memory Used", "D3D Dedicated Memory Used");
        SensorVM? total = gpu.Find(SensorKind.SmallData, "GPU Memory Total", "D3D Dedicated Memory Total");
        return (used, total);
    }

    /// <summary>The graphics adapter doing the real work: the one with the most dedicated memory.</summary>
    public static HardwareVM? PrimaryGpu(IEnumerable<HardwareVM> hardware) =>
        hardware.Where(h => h.Kind == HardwareKind.Gpu)
            .OrderByDescending(h => GpuMemory(h).Total?.Value ?? 0)
            .FirstOrDefault();

    public static SensorVM? StorageTemperature(HardwareVM drive) =>
        drive.Find(SensorKind.Temperature, "Composite Temperature", "Temperature")
        ?? drive.First(SensorKind.Temperature, s => !s.IsLimit);

    public static SensorVM? StorageUsed(HardwareVM drive) => drive.Find(SensorKind.Load, "Used Space");
    public static SensorVM? StorageActivity(HardwareVM drive) => drive.Find(SensorKind.Load, "Total Activity");
    public static SensorVM? StorageRead(HardwareVM drive) => drive.Find(SensorKind.Throughput, "Read Rate");
    public static SensorVM? StorageWrite(HardwareVM drive) => drive.Find(SensorKind.Throughput, "Write Rate");

    public static SensorVM? NetworkDown(HardwareVM nic) => nic.Find(SensorKind.Throughput, "Download Speed");
    public static SensorVM? NetworkUp(HardwareVM nic) => nic.Find(SensorKind.Throughput, "Upload Speed");

    public static float NetworkTotalData(HardwareVM nic) =>
        (nic.Find(SensorKind.Data, "Data Downloaded")?.Value ?? 0) + (nic.Find(SensorKind.Data, "Data Uploaded")?.Value ?? 0);

    /// <summary>An adapter worth showing: connected, or one that has carried traffic.</summary>
    public static bool IsActiveNic(HardwareVM nic) => nic.Nic?.IsUp == true || NetworkTotalData(nic) > 0;

    public static HardwareVM? PrimaryNic(IEnumerable<HardwareVM> hardware) =>
        hardware.Where(h => h.Kind == HardwareKind.Network && IsActiveNic(h))
            .OrderByDescending(h => h.Nic?.IsUp == true)
            .ThenByDescending(h => h.Nic?.Kind is "Ethernet" or "Wi-Fi")
            .ThenByDescending(NetworkTotalData)
            .FirstOrDefault();

    [GeneratedRegex(@"Core #(\d+)(?: Thread #(\d+))?$")]
    private static partial Regex ThreadName();
}

public sealed record FactRow(string Label, string Value);

public sealed record ModuleRow(string Slot, string Size, string Kind, string Maker, string Part);

public sealed class VolumeRow(string letter) : ObservableObject
{
    private string _title = letter;
    private string _detail = "";
    private string _percent = "";
    private double _fraction;
    private Color _color = Palette.Magenta;

    public string Letter { get; } = letter;
    public string Title { get => _title; private set => Set(ref _title, value); }
    public string Detail { get => _detail; private set => Set(ref _detail, value); }
    public string Percent { get => _percent; private set => Set(ref _percent, value); }
    public double Fraction { get => _fraction; private set => Set(ref _fraction, value); }
    public Color Color { get => _color; private set => Set(ref _color, value); }

    internal void Update(VolumeInfo volume)
    {
        double used = volume.UsedFraction;
        Title = volume.Label.Length > 0 ? $"{volume.Letter}  {volume.Label}" : volume.Letter;
        Detail = $"{SensorFormat.Bytes(volume.FreeBytes)} free of {SensorFormat.Bytes(volume.TotalBytes)} · {volume.FileSystem}";
        Percent = (used * 100).ToString("F0", CultureInfo.CurrentCulture) + "%";
        Fraction = used;
        Color = Palette.For(used >= 0.97 ? Severity.Critical : used >= 0.90 ? Severity.Warning : Severity.Normal, Palette.Magenta);
    }
}

/// <summary>One history chart: a single series, or two that share a unit (read/write, download/upload).</summary>
public sealed class ChartVM(string title, Color colorA, Color colorB, double minimum, double maximum, string? labelA, string? labelB)
    : ObservableObject
{
    private SensorVM? _a;
    private SensorVM? _b;

    public string Title { get; } = title;
    public Color ColorA { get; } = colorA;
    public Color ColorB { get; } = colorB;
    public Brush BrushA { get; } = Palette.Brush(colorA);
    public Brush BrushB { get; } = Palette.Brush(colorB);

    /// <summary>Fixed axis range, or NaN to fit the data.</summary>
    public double Minimum { get; } = minimum;
    public double Maximum { get; } = maximum;

    public string? LabelA { get; } = labelA;
    public string? LabelB { get; } = labelB;

    /// <summary>Two series need a legend; one is named by the chart's title.</summary>
    public bool IsDual => LabelB != null;

    public SensorVM? A { get => _a; internal set => Set(ref _a, value); }
    public SensorVM? B { get => _b; internal set => Set(ref _b, value); }
}

/// <summary>One device on a detail page: headline figures, history charts, facts, and its grouped sensors.</summary>
public sealed class DeviceVM : ObservableObject
{
    private readonly Dictionary<string, StatVM> _stats = [];
    private readonly Dictionary<string, ChartVM> _charts = [];
    private readonly Dictionary<string, VolumeRow> _volumes = [];
    private string _subtitle = "";
    private string? _notice;
    private bool _showDetails;
    private object? _factsSource;
    private int _detailCount = -1;

    internal DeviceVM(HardwareVM hardware)
    {
        Hardware = hardware;
        AccentBrush = Palette.Brush(hardware.AccentColor);
        AccentTint = Palette.Brush(hardware.AccentColor, 0.16);
    }

    public HardwareVM Hardware { get; }
    public Brush AccentBrush { get; }
    public Brush AccentTint { get; }

    public string Subtitle { get => _subtitle; private set => Set(ref _subtitle, value); }

    /// <summary>Explains an otherwise empty device, e.g. a motherboard whose sensors need the driver.</summary>
    public string? Notice { get => _notice; private set => Set(ref _notice, value); }

    public ObservableCollection<StatVM> Highlights { get; } = [];
    public ObservableCollection<ChartVM> Charts { get; } = [];
    public ObservableCollection<SensorVM> Threads { get; } = [];
    public ObservableCollection<FactRow> Facts { get; } = [];
    public ObservableCollection<ModuleRow> Modules { get; } = [];
    public ObservableCollection<VolumeRow> Volumes { get; } = [];
    public ObservableCollection<SensorGroupVM> Groups { get; } = [];

    /// <summary>Whether the detail-level sensors (per-engine DirectX counters) are expanded.</summary>
    public bool ShowDetails
    {
        get => _showDetails;
        set => Set(ref _showDetails, value);
    }

    public string DetailsLabel => $"DirectX engine details ({Hardware.MinorSensors.Count})";

    internal void Update(MonitorContext context)
    {
        var highlights = new List<StatVM>(5);
        var charts = new List<ChartVM>(2);
        Func<SensorGroupVM, bool> showGroup = _ => true;

        switch (Hardware.Kind)
        {
            case HardwareKind.Cpu:
                showGroup = UpdateCpu(context, highlights, charts);
                break;
            case HardwareKind.Gpu:
                UpdateGpu(context, highlights, charts);
                break;
            case HardwareKind.Memory when Hardware.Find(SensorKind.Load, "Memory") != null:
                showGroup = UpdateMemory(context, highlights, charts);
                break;
            case HardwareKind.Storage:
                UpdateStorage(context, highlights, charts);
                break;
            case HardwareKind.Network:
                showGroup = UpdateNetwork(highlights, charts);
                break;
            case HardwareKind.Motherboard:
                UpdateMotherboard(context);
                break;
            default:
                UpdateGeneric(highlights, charts);
                break;
        }

        Highlights.SyncTo(highlights);
        Charts.SyncTo(charts);
        Groups.SyncTo(Hardware.Groups.Where(showGroup).ToList());

        int detailCount = Hardware.MinorSensors.Count;
        if (detailCount != _detailCount)
        {
            _detailCount = detailCount;
            Raise(nameof(DetailsLabel));
        }
    }

    private Func<SensorGroupVM, bool> UpdateCpu(MonitorContext context, List<StatVM> highlights, List<ChartVM> charts)
    {
        SensorVM? load = Pick.CpuLoad(Hardware);
        SensorVM? temperature = Pick.CpuTemperature(Hardware);
        SensorVM? power = Pick.CpuPower(Hardware);

        Stat("Load", meter: true).ShowSensor(load);
        Stat("Temperature", meter: true).ShowSensor(temperature, context.MissingHint);
        Stat("Power").ShowSensor(power, context.MissingHint);

        StatVM speed = Stat("Speed");
        if (Pick.CpuSpeed(Hardware) is float mhz)
        {
            (string number, string unit) = SensorFormat.Split(SensorKind.Clock, mhz);
            speed.Show(number, unit, Hardware.AccentColor);
        }
        else
        {
            speed.ShowMissing(context.MissingHint);
        }

        highlights.AddRange([Stat("Load"), Stat("Temperature"), Stat("Power"), Stat("Speed")]);

        if (load != null)
            charts.Add(Chart("Total load", load, 0, 100));
        if (temperature != null)
            charts.Add(Chart("Temperature", temperature));

        List<SensorVM> threads = Pick.CpuThreads(Hardware);
        Threads.SyncTo(threads);

        SmbiosCpu? info = context.Inventory?.Smbios.Cpus.FirstOrDefault();
        if (info != null && !ReferenceEquals(_factsSource, info))
        {
            _factsSource = info;
            var parts = new List<string>();
            if (info.Cores > 0)
                parts.Add($"{info.Cores} cores");
            if (info.Threads > 0)
                parts.Add($"{info.Threads} threads");
            if (info.Socket.Length > 0)
                parts.Add($"Socket {info.Socket}");
            if (info.MaxMhz > 0)
                parts.Add($"up to {(info.MaxMhz / 1000.0).ToString("0.##", CultureInfo.CurrentCulture)} GHz");
            Subtitle = string.Join("  ·  ", parts);

            var facts = new List<FactRow>();
            AddCache(facts, "L1 cache", info.L1Kb);
            AddCache(facts, "L2 cache", info.L2Kb);
            AddCache(facts, "L3 cache", info.L3Kb);
            Facts.SyncTo(facts);
        }

        // Per-thread load has its own grid, and the total is a headline figure.
        return threads.Count > 0 ? group => group.Title != SensorFormat.GroupTitle(SensorKind.Load) : _ => true;
    }

    private void UpdateGpu(MonitorContext context, List<StatVM> highlights, List<ChartVM> charts)
    {
        SensorVM? load = Pick.GpuLoad(Hardware);
        SensorVM? temperature = Pick.GpuTemperature(Hardware);
        SensorVM? power = Pick.GpuPower(Hardware);
        SensorVM? fan = Pick.GpuFan(Hardware);

        Stat("Load", meter: true).ShowSensor(load);
        highlights.Add(Stat("Load"));

        if (temperature != null)
        {
            Stat("Temperature", meter: true).ShowSensor(temperature);
            highlights.Add(Stat("Temperature"));
        }

        ShowGpuMemory(Stat("Memory", meter: true), Hardware);
        highlights.Add(Stat("Memory"));

        if (power != null)
        {
            Stat("Power").ShowSensor(power);
            highlights.Add(Stat("Power"));
        }

        if (fan != null && highlights.Count < 5)
        {
            Stat("Fan").ShowSensor(fan);
            highlights.Add(Stat("Fan"));
        }

        if (load != null)
            charts.Add(Chart("Load", load, 0, 100));
        if (temperature != null)
            charts.Add(Chart("Temperature", temperature));

        GpuInfo? info = context.Inventory?.Gpus.FirstOrDefault(g => g.Name.Equals(Hardware.Name, StringComparison.OrdinalIgnoreCase));
        if (info != null && !ReferenceEquals(_factsSource, info))
        {
            _factsSource = info;
            var parts = new List<string>();
            if (info.VramBytes > 0)
                parts.Add($"{SensorFormat.Bytes(info.VramBytes)} memory");
            if (info.Width > 0)
                parts.Add($"{info.Width} × {info.Height} @ {info.RefreshHz} Hz");
            if (info.DriverVersion.Length > 0)
                parts.Add($"Driver {info.DriverVersion}" + (info.DriverDate is DateTime date ? $" ({date.ToString("d", CultureInfo.CurrentCulture)})" : ""));
            Subtitle = string.Join("  ·  ", parts);
        }
    }

    internal static void ShowGpuMemory(StatVM stat, HardwareVM gpu)
    {
        (SensorVM? used, SensorVM? total) = Pick.GpuMemory(gpu);
        if (used?.Value is not float usedMb)
        {
            stat.ShowMissing();
            return;
        }

        if (total?.Value is float totalMb && totalMb > 0)
        {
            string format = totalMb >= 10240 ? "F1" : "F2";
            stat.Show(
                (usedMb / 1024).ToString(format, CultureInfo.CurrentCulture),
                $"/ {(totalMb / 1024).ToString("0.#", CultureInfo.CurrentCulture)} GB",
                gpu.AccentColor,
                Math.Clamp(usedMb / totalMb, 0, 1));
        }
        else
        {
            (string number, string unit) = SensorFormat.Split(SensorKind.SmallData, usedMb);
            stat.Show(number, unit, gpu.AccentColor);
        }
    }

    private Func<SensorGroupVM, bool> UpdateMemory(MonitorContext context, List<StatVM> highlights, List<ChartVM> charts)
    {
        SensorVM? load = Hardware.Find(SensorKind.Load, "Memory");
        Stat("In use", meter: true).ShowSensor(load);
        Stat("Used").ShowSensor(Hardware.Find(SensorKind.Data, "Memory Used"));
        Stat("Available").ShowSensor(Hardware.Find(SensorKind.Data, "Memory Available"));
        highlights.AddRange([Stat("In use"), Stat("Used"), Stat("Available")]);

        if (load != null)
            charts.Add(Chart("In use", load, 0, 100));

        bool physical = Hardware.Key == "/ram";
        if (!physical)
        {
            Subtitle = "Physical memory plus the page file";
        }
        else if (context.Inventory is { } inventory && !ReferenceEquals(_factsSource, inventory))
        {
            _factsSource = inventory;
            List<SmbiosMemory> installed = inventory.Smbios.Memory.Where(m => m.SizeMb > 0).ToList();
            if (installed.Count > 0)
            {
                Subtitle = $"{inventory.InstalledMemoryMb / 1024.0:0.#} GB installed  ·  {TypeAndSpeed(installed[0])}  ·  " +
                           $"{installed.Count} of {inventory.Smbios.Memory.Count} slots used";

                Modules.SyncTo(installed.Select(m => new ModuleRow(
                    m.Slot.Length > 0 && m.Bank.Length > 0 ? $"{m.Bank} / {m.Slot}" : m.Slot + m.Bank,
                    $"{m.SizeMb / 1024.0:0.#} GB",
                    TypeAndSpeed(m),
                    m.Maker,
                    m.Part)).ToList());
            }
        }

        // Usage and amounts are already headline figures; anything else (module temperatures) is listed.
        return group => group.Title != SensorFormat.GroupTitle(SensorKind.Load) && group.Title != SensorFormat.GroupTitle(SensorKind.Data);
    }

    /// <summary>"DDR5-6000", using the speed the module is actually running at.</summary>
    private static string TypeAndSpeed(SmbiosMemory module)
    {
        int speed = module.ConfiguredMhz > 0 ? module.ConfiguredMhz : module.SpeedMhz;
        return speed > 0 ? $"{module.Type}-{speed}" : module.Type;
    }

    private void UpdateStorage(MonitorContext context, List<StatVM> highlights, List<ChartVM> charts)
    {
        SensorVM? temperature = Pick.StorageTemperature(Hardware);
        SensorVM? used = Pick.StorageUsed(Hardware);
        SensorVM? read = Pick.StorageRead(Hardware);
        SensorVM? write = Pick.StorageWrite(Hardware);
        DiskInfo? disk = Hardware.Disk;

        if (temperature != null || context.Access.Level == AccessLevel.Limited)
        {
            Stat("Temperature", meter: true).ShowSensor(temperature, "Not reported by this drive without administrator rights");
            highlights.Add(Stat("Temperature"));
        }

        if (used != null)
        {
            string? free = disk is { Volumes.Count: > 0 } ? SensorFormat.Bytes(disk.Volumes.Sum(v => v.FreeBytes)) + " free" : null;
            Stat("Used space", meter: true).ShowSensor(used, detail: free);
            highlights.Add(Stat("Used space"));
        }

        Stat("Read").ShowSensor(read);
        Stat("Write").ShowSensor(write);
        highlights.AddRange([Stat("Read"), Stat("Write")]);

        if (read != null || write != null)
            charts.Add(Chart("Transfer rate", read, b: write, labelA: "Read", labelB: "Write", colorA: Palette.Blue));
        if (temperature != null)
            charts.Add(Chart("Temperature", temperature));

        // Drive facts are replaced wholesale when re-read, so a new reference is the only time to rebuild.
        if (disk != null && !ReferenceEquals(disk, _factsSource))
        {
            _factsSource = disk;
            var parts = new List<string> { disk.Kind };
            if (disk.SizeBytes > 0)
                parts.Add(SensorFormat.Capacity(disk.SizeBytes));
            if (disk.Firmware.Length > 0)
                parts.Add($"Firmware {disk.Firmware}");
            Subtitle = string.Join("  ·  ", parts);

            var rows = new List<VolumeRow>(disk.Volumes.Count);
            foreach (VolumeInfo volume in disk.Volumes)
            {
                if (!_volumes.TryGetValue(volume.Letter, out VolumeRow? row))
                    _volumes[volume.Letter] = row = new VolumeRow(volume.Letter);

                row.Update(volume);
                rows.Add(row);
            }

            Volumes.SyncTo(rows);
        }
    }

    private Func<SensorGroupVM, bool> UpdateNetwork(List<StatVM> highlights, List<ChartVM> charts)
    {
        SensorVM? down = Pick.NetworkDown(Hardware);
        SensorVM? up = Pick.NetworkUp(Hardware);

        Stat("Download").ShowSensor(down);
        Stat("Upload").ShowSensor(up);
        Stat("Received").ShowSensor(Hardware.Find(SensorKind.Data, "Data Downloaded"));
        Stat("Sent").ShowSensor(Hardware.Find(SensorKind.Data, "Data Uploaded"));
        highlights.AddRange([Stat("Download"), Stat("Upload"), Stat("Received"), Stat("Sent")]);

        charts.Add(Chart("Throughput", down, b: up, labelA: "Download", labelB: "Upload", colorA: Palette.Blue));

        if (Hardware.Nic is { } nic && !ReferenceEquals(nic, _factsSource))
        {
            _factsSource = nic;
            var parts = new List<string> { nic.Description };
            parts.Add(nic.IsUp ? nic.Kind : $"{nic.Kind}, disconnected");
            if (nic.IsUp && nic.SpeedBitsPerSecond > 0)
                parts.Add(SensorFormat.LinkSpeed(nic.SpeedBitsPerSecond));
            if (nic.IPv4.Length > 0)
                parts.Add(nic.IPv4);
            Subtitle = string.Join("  ·  ", parts);
        }

        // Speeds and totals are headline figures; only utilization is left for the list.
        return group => group.Title == SensorFormat.GroupTitle(SensorKind.Load);
    }

    private void UpdateMotherboard(MonitorContext context)
    {
        if (context.Inventory is { } inventory && !ReferenceEquals(_factsSource, inventory))
        {
            _factsSource = inventory;
            SmbiosData smbios = inventory.Smbios;
            Subtitle = smbios.BiosVersion.Length > 0
                ? $"BIOS {smbios.BiosVersion}" + (smbios.BiosDate is DateTime date ? $"  ·  {date.ToString("d", CultureInfo.CurrentCulture)}" : "")
                : "";

            var facts = new List<FactRow>();
            AddFact(facts, "Manufacturer", smbios.BoardMaker);
            AddFact(facts, "Model", smbios.BoardModel);
            AddFact(facts, "Revision", smbios.BoardVersion);
            AddFact(facts, "BIOS vendor", smbios.BiosVendor);
            Facts.SyncTo(facts);
        }

        bool hasSensors = Hardware.Sensors.Count > 0
                          || context.Hardware.Any(h => h.ParentKey == Hardware.Key && h.Sensors.Count > 0);

        Notice = hasSensors ? null : context.Access.Level switch
        {
            AccessLevel.Limited =>
                "Fan speeds, voltages and board temperatures are read through a low-level driver and need administrator rights.",
            AccessLevel.DriverMissing =>
                "Fan speeds, voltages and board temperatures need the PawnIO sensor driver. You can install it from Settings.",
            AccessLevel.DriverUnavailable =>
                "The PawnIO sensor driver is installed but not responding. Restarting Windows usually fixes this.",
            _ => "This motherboard's monitoring chip is not supported by the sensor library yet.",
        };
    }

    private void UpdateGeneric(List<StatVM> highlights, List<ChartVM> charts)
    {
        SensorVM? temperature = Hardware.First(SensorKind.Temperature, s => !s.IsLimit);
        SensorVM? fan = Hardware.First(SensorKind.Fan);
        SensorVM? power = Hardware.First(SensorKind.Power);
        SensorVM? level = Hardware.First(SensorKind.Level);

        if (temperature != null)
        {
            Stat(temperature.Name, meter: true).ShowSensor(temperature);
            highlights.Add(Stat(temperature.Name));
            charts.Add(Chart(temperature.Name, temperature));
        }

        if (fan != null)
        {
            Stat(fan.Name).ShowSensor(fan);
            highlights.Add(Stat(fan.Name));
        }

        if (power != null)
        {
            Stat(power.Name).ShowSensor(power);
            highlights.Add(Stat(power.Name));
        }

        if (level != null)
        {
            Stat(level.Name, meter: true).ShowSensor(level);
            highlights.Add(Stat(level.Name));
        }

        Subtitle = Hardware.Kind switch
        {
            HardwareKind.SuperIO => "Motherboard monitoring chip",
            HardwareKind.EmbeddedController => "Embedded controller",
            HardwareKind.Cooler => "Cooling controller",
            HardwareKind.Psu => "Power supply",
            HardwareKind.Battery => "Battery",
            HardwareKind.PowerMonitor => "Power monitor",
            HardwareKind.Memory => "Memory module",
            _ => "",
        };
    }

    private StatVM Stat(string label, bool meter = false)
    {
        if (!_stats.TryGetValue(label, out StatVM? stat))
            _stats[label] = stat = new StatVM(label, meter);

        return stat;
    }

    private ChartVM Chart(
        string title, SensorVM? a, double minimum = double.NaN, double maximum = double.NaN,
        SensorVM? b = null, string? labelA = null, string? labelB = null, Color? colorA = null)
    {
        if (!_charts.TryGetValue(title, out ChartVM? chart))
            _charts[title] = chart = new ChartVM(title, colorA ?? Hardware.AccentColor, Palette.Orange, minimum, maximum, labelA, labelB);

        chart.A = a;
        chart.B = b;
        return chart;
    }

    private static void AddFact(List<FactRow> facts, string label, string value)
    {
        if (value.Length > 0)
            facts.Add(new FactRow(label, value));
    }

    private static void AddCache(List<FactRow> facts, string label, long kilobytes)
    {
        if (kilobytes > 0)
            facts.Add(new FactRow(label, kilobytes >= 1024 ? $"{kilobytes / 1024.0:0.#} MB" : $"{kilobytes} KB"));
    }
}

/// <summary>A page listing every device of one category.</summary>
public sealed class DevicePageVM(string title, string emptyText, Func<HardwareVM, MonitorContext, bool> filter, Func<IEnumerable<HardwareVM>, IEnumerable<HardwareVM>>? order = null)
    : ObservableObject
{
    private readonly Dictionary<string, DeviceVM> _devices = [];
    private bool _isEmpty = true;

    public string Title { get; } = title;
    public string EmptyText { get; } = emptyText;
    public ObservableCollection<DeviceVM> Devices { get; } = [];
    public bool IsEmpty { get => _isEmpty; private set => Set(ref _isEmpty, value); }

    internal void Update(MonitorContext context)
    {
        IEnumerable<HardwareVM> wanted = context.Hardware.Where(h => filter(h, context));
        if (order != null)
            wanted = order(wanted);

        var list = new List<DeviceVM>();
        foreach (HardwareVM hardware in wanted)
        {
            if (!_devices.TryGetValue(hardware.Key, out DeviceVM? device) || !ReferenceEquals(device.Hardware, hardware))
                _devices[hardware.Key] = device = new DeviceVM(hardware);

            device.Update(context);
            list.Add(device);
        }

        if (_devices.Count > list.Count)
        {
            var keep = list.Select(d => d.Hardware.Key).ToHashSet();
            foreach (string key in _devices.Keys.Where(k => !keep.Contains(k)).ToList())
                _devices.Remove(key);
        }

        Devices.SyncTo(list);
        IsEmpty = list.Count == 0;
    }
}
