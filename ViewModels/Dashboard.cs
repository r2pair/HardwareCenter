using System.Collections.ObjectModel;
using System.Windows.Media;
using HardwareCenter.Core;

namespace HardwareCenter.ViewModels;

/// <summary>One of the headline tiles on the overview: a gauge, three figures and a trend line.</summary>
public sealed class HeroTileVM : ObservableObject
{
    private string _device = "";
    private SensorVM? _trend;

    public HeroTileVM(string title, string iconKey, Color accent, string primaryLabel, params string[] statLabels)
    {
        Title = title;
        IconKey = iconKey;
        AccentColor = accent;
        AccentBrush = Palette.Brush(accent);
        AccentTint = Palette.Brush(accent, 0.16);
        Primary = new StatVM(primaryLabel, hasMeter: true);
        Stats = statLabels.Select(label => new StatVM(label)).ToArray();
    }

    public string Title { get; }
    public string IconKey { get; }
    public Color AccentColor { get; }
    public Brush AccentBrush { get; }
    public Brush AccentTint { get; }
    public StatVM Primary { get; }
    public IReadOnlyList<StatVM> Stats { get; }

    public string Device { get => _device; internal set => Set(ref _device, value); }
    public SensorVM? Trend { get => _trend; internal set => Set(ref _trend, value); }
}

/// <summary>A sensor shown under a friendlier name than its own, e.g. "CPU" for "Core (Tctl/Tdie)".</summary>
public sealed class LabeledSensor(SensorVM sensor) : ObservableObject
{
    private string _label = sensor.Name;

    public SensorVM Sensor { get; } = sensor;
    public string Label { get => _label; internal set => Set(ref _label, value); }
}

/// <summary>A fan's speed with, when the device reports it, the duty cycle driving it.</summary>
public sealed class FanRow(SensorVM speed) : ObservableObject
{
    private string _label = speed.Name;
    private SensorVM? _control;

    public SensorVM Speed { get; } = speed;
    public string Label { get => _label; internal set => Set(ref _label, value); }

    public SensorVM? Control
    {
        get => _control;
        internal set
        {
            if (Set(ref _control, value))
                Raise(nameof(HasControl));
        }
    }

    public bool HasControl => _control != null;
}

public sealed class DriveRow(HardwareVM hardware) : ObservableObject
{
    private string _detail = "";
    private SensorVM? _used, _temperature, _read, _write;

    public HardwareVM Hardware { get; } = hardware;
    public string Detail { get => _detail; private set => Set(ref _detail, value); }
    public SensorVM? Used { get => _used; private set => Set(ref _used, value); }
    public SensorVM? Temperature { get => _temperature; private set => Set(ref _temperature, value); }
    public SensorVM? Read { get => _read; private set => Set(ref _read, value); }
    public SensorVM? Write { get => _write; private set => Set(ref _write, value); }

    internal void Update()
    {
        Used = Pick.StorageUsed(Hardware);
        Temperature = Pick.StorageTemperature(Hardware);
        Read = Pick.StorageRead(Hardware);
        Write = Pick.StorageWrite(Hardware);

        var parts = new List<string>(4);
        if (Used?.Value != null)
            parts.Add($"{Used.CompactText} used");

        if (Hardware.Disk is { } disk)
        {
            if (disk.Volumes.Count > 0)
                parts.Add($"{SensorFormat.Bytes(disk.Volumes.Sum(v => v.FreeBytes))} free");

            parts.Add(disk.SizeBytes > 0 ? $"{disk.Kind}, {SensorFormat.Capacity(disk.SizeBytes)}" : disk.Kind);
        }

        Detail = string.Join("  ·  ", parts);
    }
}

public sealed class NetworkTileVM : ObservableObject
{
    private string _name = "No active connection";
    private string _detail = "";
    private ChartVM? _chart;
    private HardwareVM? _adapter;

    public StatVM Download { get; } = new("Download");
    public StatVM Upload { get; } = new("Upload");
    public Brush DownloadBrush { get; } = Palette.Brush(Palette.Blue);
    public Brush UploadBrush { get; } = Palette.Brush(Palette.Orange);

    public string Name { get => _name; private set => Set(ref _name, value); }
    public string Detail { get => _detail; private set => Set(ref _detail, value); }
    public ChartVM? Chart { get => _chart; private set => Set(ref _chart, value); }

    internal void Update(HardwareVM? adapter)
    {
        if (!ReferenceEquals(adapter, _adapter))
        {
            _adapter = adapter;
            Chart = adapter == null
                ? null
                : new ChartVM("Throughput", Palette.Blue, Palette.Orange, double.NaN, double.NaN, "Download", "Upload");
        }

        if (adapter == null)
        {
            Name = "No active connection";
            Detail = "";
            Download.ShowMissing();
            Upload.ShowMissing();
            return;
        }

        SensorVM? down = Pick.NetworkDown(adapter);
        SensorVM? up = Pick.NetworkUp(adapter);
        Download.ShowSensor(down);
        Upload.ShowSensor(up);

        if (Chart != null)
        {
            Chart.A = down;
            Chart.B = up;
        }

        Name = adapter.Name;
        if (adapter.Nic is { } nic)
        {
            var parts = new List<string> { nic.Kind };
            if (nic.SpeedBitsPerSecond > 0)
                parts.Add(SensorFormat.LinkSpeed(nic.SpeedBitsPerSecond));
            if (nic.IPv4.Length > 0)
                parts.Add(nic.IPv4);
            Detail = string.Join("  ·  ", parts);
        }
    }
}

/// <summary>Collects temperature and fan sensors from every device into friendly, stable lists.</summary>
internal sealed class ThermalIndex
{
    private readonly Dictionary<SensorVM, LabeledSensor> _labeled = [];
    private readonly Dictionary<SensorVM, FanRow> _fans = [];

    /// <summary>The temperatures worth a glance: one per major component, plus the board's own sensors.</summary>
    public List<LabeledSensor> KeyTemperatures(IReadOnlyList<HardwareVM> hardware, int limit)
    {
        var rows = new List<LabeledSensor>();
        int gpuCount = hardware.Count(h => h.Kind == HardwareKind.Gpu && Pick.GpuTemperature(h) != null);

        foreach (HardwareVM cpu in hardware.Where(h => h.Kind == HardwareKind.Cpu))
            Add(rows, Pick.CpuTemperature(cpu), "CPU");

        foreach (HardwareVM gpu in hardware.Where(h => h.Kind == HardwareKind.Gpu))
        {
            string name = gpuCount > 1 ? ShortGpuName(gpu.Name) : "GPU";
            Add(rows, Pick.GpuTemperature(gpu), name);
            Add(rows, gpu.Find(SensorKind.Temperature, "GPU Hot Spot"), $"{name} hot spot");
        }

        foreach (HardwareVM drive in hardware.Where(h => h.Kind == HardwareKind.Storage))
            Add(rows, Pick.StorageTemperature(drive), drive.Name);

        foreach (HardwareVM board in hardware.Where(h => h.Kind is HardwareKind.SuperIO or HardwareKind.EmbeddedController or HardwareKind.Motherboard))
        {
            foreach (SensorVM sensor in board.OfKind(SensorKind.Temperature).Where(IsPlausible).Take(5))
                Add(rows, sensor, sensor.Name);
        }

        foreach (HardwareVM other in hardware.Where(h => h.Kind is HardwareKind.Memory or HardwareKind.Cooler or HardwareKind.Psu or HardwareKind.Battery))
            Add(rows, other.First(SensorKind.Temperature, s => !s.IsLimit), other.Name);

        return rows.Count > limit ? rows.GetRange(0, limit) : rows;
    }

    /// <summary>Every fan that has spun at least once (unconnected headers read 0 forever and are skipped).</summary>
    public List<FanRow> Fans(IReadOnlyList<HardwareVM> hardware, int limit)
    {
        var rows = new List<FanRow>();
        foreach (HardwareVM device in hardware)
        {
            foreach (SensorVM fan in device.OfKind(SensorKind.Fan))
            {
                if (fan.Max is not > 0)
                    continue;

                if (!_fans.TryGetValue(fan, out FanRow? row))
                    _fans[fan] = row = new FanRow(fan);

                row.Control = device.Find(SensorKind.Control, fan.Name)
                              ?? device.OfKind(SensorKind.Control).FirstOrDefault(c => c.Index == fan.Index);
                rows.Add(row);
            }
        }

        return rows.Count > limit ? rows.GetRange(0, limit) : rows;
    }

    public LabeledSensor Labeled(SensorVM sensor, string label)
    {
        if (!_labeled.TryGetValue(sensor, out LabeledSensor? row))
            _labeled[sensor] = row = new LabeledSensor(sensor);

        row.Label = label;
        return row;
    }

    /// <summary>Drops rows for sensors that no longer exist, so removed devices can be collected.</summary>
    public void Prune(IReadOnlyList<HardwareVM> hardware)
    {
        if (_labeled.Count == 0 && _fans.Count == 0)
            return;

        var alive = new HashSet<HardwareVM>(hardware);
        foreach (SensorVM sensor in _labeled.Keys.Where(s => !alive.Contains(s.Owner) || !s.Owner.Sensors.Contains(s)).ToList())
            _labeled.Remove(sensor);
        foreach (SensorVM sensor in _fans.Keys.Where(s => !alive.Contains(s.Owner) || !s.Owner.Sensors.Contains(s)).ToList())
            _fans.Remove(sensor);
    }

    /// <summary>Unconnected thermistor headers read 0 °C or absurd values.</summary>
    public static bool IsPlausible(SensorVM sensor) =>
        !sensor.IsLimit && sensor.Value is > 0 and < 150;

    public static string ShortGpuName(string name) =>
        name.Replace("NVIDIA GeForce ", "", StringComparison.OrdinalIgnoreCase)
            .Replace("AMD Radeon(TM) ", "Radeon ", StringComparison.OrdinalIgnoreCase)
            .Replace("AMD Radeon ", "Radeon ", StringComparison.OrdinalIgnoreCase)
            .Replace("Intel(R) ", "", StringComparison.OrdinalIgnoreCase);

    private void Add(List<LabeledSensor> rows, SensorVM? sensor, string label)
    {
        if (sensor != null && !rows.Any(r => ReferenceEquals(r.Sensor, sensor)))
            rows.Add(Labeled(sensor, label));
    }
}

public sealed class DashboardVM : ObservableObject
{
    private readonly ThermalIndex _thermals = new();
    private readonly Dictionary<HardwareVM, DriveRow> _driveRows = [];
    private string? _sensorNote;
    private bool _hasFans, _hasTemperatures, _hasDrives;
    private int _pruneCountdown;

    public HeroTileVM Cpu { get; } = new("Processor", "Cpu", Palette.Blue, "Load", "Temperature", "Power", "Speed");
    public HeroTileVM Gpu { get; } = new("Graphics", "Gpu", Palette.Aqua, "Load", "Temperature", "Power", "Memory");
    public HeroTileVM Memory { get; } = new("Memory", "Memory", Palette.Violet, "In use", "Used", "Available", "Virtual");

    public IReadOnlyList<HeroTileVM> Heroes { get; }
    public ObservableCollection<LabeledSensor> Temperatures { get; } = [];
    public ObservableCollection<FanRow> Fans { get; } = [];
    public ObservableCollection<DriveRow> Drives { get; } = [];
    public NetworkTileVM Network { get; } = new();

    public DashboardVM()
    {
        Heroes = [Cpu, Gpu, Memory];
    }

    public bool HasTemperatures { get => _hasTemperatures; private set => Set(ref _hasTemperatures, value); }
    public bool HasFans { get => _hasFans; private set => Set(ref _hasFans, value); }
    public bool HasDrives { get => _hasDrives; private set => Set(ref _hasDrives, value); }

    /// <summary>Why some temperatures and fans are missing, or null when everything is readable.</summary>
    public string? SensorNote { get => _sensorNote; private set => Set(ref _sensorNote, value); }

    internal void Update(MonitorContext context)
    {
        IReadOnlyList<HardwareVM> hardware = context.Hardware;
        string hint = context.MissingHint;

        // ---- Processor ----
        HardwareVM? cpu = hardware.FirstOrDefault(h => h.Kind == HardwareKind.Cpu);
        if (cpu != null)
        {
            SensorVM? load = Pick.CpuLoad(cpu);
            Cpu.Device = cpu.Name;
            Cpu.Trend = load;
            Cpu.Primary.ShowSensor(load);
            Cpu.Stats[0].ShowSensor(Pick.CpuTemperature(cpu), hint);
            Cpu.Stats[1].ShowSensor(Pick.CpuPower(cpu), hint);
            if (Pick.CpuSpeed(cpu) is float mhz)
            {
                (string number, string unit) = SensorFormat.Split(SensorKind.Clock, mhz);
                Cpu.Stats[2].Show(number, unit, cpu.AccentColor);
            }
            else
            {
                Cpu.Stats[2].ShowMissing(hint);
            }
        }

        // ---- Graphics ----
        HardwareVM? gpu = Pick.PrimaryGpu(hardware);
        if (gpu != null)
        {
            SensorVM? load = Pick.GpuLoad(gpu);
            Gpu.Device = gpu.Name;
            Gpu.Trend = load;
            Gpu.Primary.ShowSensor(load);
            Gpu.Stats[0].ShowSensor(Pick.GpuTemperature(gpu), "Not reported by this adapter");
            Gpu.Stats[1].ShowSensor(Pick.GpuPower(gpu), "Not reported by this adapter");
            DeviceVM.ShowGpuMemory(Gpu.Stats[2], gpu);
        }

        // ---- Memory ----
        HardwareVM? ram = hardware.FirstOrDefault(h => h.Key == "/ram")
                          ?? hardware.FirstOrDefault(h => h.Kind == HardwareKind.Memory && h.Find(SensorKind.Load, "Memory") != null);
        if (ram != null)
        {
            SensorVM? load = ram.Find(SensorKind.Load, "Memory");
            Memory.Trend = load;
            Memory.Primary.ShowSensor(load);
            Memory.Stats[0].ShowSensor(ram.Find(SensorKind.Data, "Memory Used"));
            Memory.Stats[1].ShowSensor(ram.Find(SensorKind.Data, "Memory Available"));

            HardwareVM? virtualMemory = hardware.FirstOrDefault(h => h.Key == "/vram");
            Memory.Stats[2].ShowSensor(virtualMemory?.Find(SensorKind.Load, "Memory"));

            Memory.Device = DescribeMemory(context.Inventory, ram);
        }

        // ---- Lists ----
        Temperatures.SyncTo(_thermals.KeyTemperatures(hardware, limit: 9));
        Fans.SyncTo(_thermals.Fans(hardware, limit: 8));
        HasTemperatures = Temperatures.Count > 0;
        HasFans = Fans.Count > 0;

        var drives = new List<DriveRow>();
        foreach (HardwareVM drive in hardware.Where(h => h.Kind == HardwareKind.Storage))
        {
            if (!_driveRows.TryGetValue(drive, out DriveRow? row))
                _driveRows[drive] = row = new DriveRow(drive);

            row.Update();
            drives.Add(row);
        }

        Drives.SyncTo(drives);
        HasDrives = drives.Count > 0;

        Network.Update(Pick.PrimaryNic(hardware));

        SensorNote = context.Access.Level switch
        {
            AccessLevel.Limited => "CPU, motherboard and case-fan sensors appear when the app runs as administrator.",
            AccessLevel.DriverMissing => "CPU, motherboard and case-fan sensors appear once the PawnIO sensor driver is installed.",
            AccessLevel.DriverUnavailable => "The sensor driver is not responding, so CPU and motherboard sensors are unavailable.",
            _ => null,
        };

        // Forget rows for hardware that has gone away (rare, so only checked now and then).
        if (--_pruneCountdown <= 0)
        {
            _pruneCountdown = 30;
            _thermals.Prune(hardware);
            foreach (HardwareVM gone in _driveRows.Keys.Where(k => !hardware.Contains(k)).ToList())
                _driveRows.Remove(gone);
        }
    }

    private static string DescribeMemory(Inventory? inventory, HardwareVM ram)
    {
        List<SmbiosMemory>? modules = inventory?.Smbios.Memory.Where(m => m.SizeMb > 0).ToList();
        if (modules is { Count: > 0 })
        {
            SmbiosMemory first = modules[0];
            int speed = first.ConfiguredMhz > 0 ? first.ConfiguredMhz : first.SpeedMhz;
            return $"{modules.Sum(m => m.SizeMb) / 1024.0:0.#} GB {first.Type}{(speed > 0 ? "-" + speed : "")}";
        }

        float total = (ram.Find(SensorKind.Data, "Memory Used")?.Value ?? 0) + (ram.Find(SensorKind.Data, "Memory Available")?.Value ?? 0);
        return total > 0 ? $"{total:0.#} GB usable" : "";
    }
}

/// <summary>All temperature sensors on one device, for the thermals page.</summary>
public sealed class ThermalGroupVM(HardwareVM hardware)
{
    public HardwareVM Hardware { get; } = hardware;
    public ObservableCollection<SensorVM> Sensors { get; } = [];
}

/// <summary>Every temperature and every fan in one place.</summary>
public sealed class ThermalsPageVM : ObservableObject
{
    private readonly ThermalIndex _index = new();
    private readonly Dictionary<HardwareVM, ThermalGroupVM> _groups = [];
    private readonly StatVM _hottest = new("Hottest right now", hasMeter: true);
    private ChartVM? _chart;
    private SensorVM? _chartCpu, _chartGpu;
    private string? _note;
    private bool _hasFans, _hasTemperatures;
    private int _pruneCountdown;

    public string Title => "Thermals";
    public ObservableCollection<ThermalGroupVM> Groups { get; } = [];
    public ObservableCollection<FanRow> Fans { get; } = [];
    public StatVM Hottest => _hottest;

    public ChartVM? Chart { get => _chart; private set => Set(ref _chart, value); }
    public string? Note { get => _note; private set => Set(ref _note, value); }
    public bool HasFans { get => _hasFans; private set => Set(ref _hasFans, value); }
    public bool HasTemperatures { get => _hasTemperatures; private set => Set(ref _hasTemperatures, value); }

    internal void Update(MonitorContext context)
    {
        IReadOnlyList<HardwareVM> hardware = context.Hardware;

        var groups = new List<ThermalGroupVM>();
        SensorVM? hottest = null;
        foreach (HardwareVM device in hardware)
        {
            List<SensorVM> sensors = device.OfKind(SensorKind.Temperature).Where(ThermalIndex.IsPlausible).ToList();
            if (sensors.Count == 0)
                continue;

            if (!_groups.TryGetValue(device, out ThermalGroupVM? group))
                _groups[device] = group = new ThermalGroupVM(device);

            group.Sensors.SyncTo(sensors);
            groups.Add(group);

            foreach (SensorVM sensor in sensors)
            {
                if (hottest == null || sensor.Value > hottest.Value)
                    hottest = sensor;
            }
        }

        Groups.SyncTo(groups);
        HasTemperatures = groups.Count > 0;

        if (hottest != null)
            _hottest.ShowSensor(hottest, detail: $"{hottest.Owner.Name}  ·  {hottest.Name}");
        else
            _hottest.ShowMissing();

        Fans.SyncTo(_index.Fans(hardware, limit: 24));
        HasFans = Fans.Count > 0;

        // CPU against GPU temperature: one unit, one axis, two entities in their own colors.
        HardwareVM? cpu = hardware.FirstOrDefault(h => h.Kind == HardwareKind.Cpu);
        HardwareVM? gpu = Pick.PrimaryGpu(hardware);
        SensorVM? cpuTemperature = cpu == null ? null : Pick.CpuTemperature(cpu);
        SensorVM? gpuTemperature = gpu == null ? null : Pick.GpuTemperature(gpu);

        if (!ReferenceEquals(cpuTemperature, _chartCpu) || !ReferenceEquals(gpuTemperature, _chartGpu))
        {
            _chartCpu = cpuTemperature;
            _chartGpu = gpuTemperature;

            if (cpuTemperature != null && gpuTemperature != null)
            {
                Chart = new ChartVM("CPU and GPU temperature", Palette.Blue, Palette.Aqua, double.NaN, double.NaN, "CPU", "GPU")
                {
                    A = cpuTemperature,
                    B = gpuTemperature,
                };
            }
            else if ((cpuTemperature ?? gpuTemperature) is { } only)
            {
                Chart = new ChartVM(
                    cpuTemperature != null ? "CPU temperature" : "GPU temperature",
                    only.Owner.AccentColor, Palette.Orange, double.NaN, double.NaN, null, null)
                {
                    A = only,
                };
            }
            else
            {
                Chart = null;
            }
        }

        Note = context.Access.Level switch
        {
            AccessLevel.Limited => "Running without administrator rights: CPU temperatures and motherboard fans are not available.",
            AccessLevel.DriverMissing => "The PawnIO sensor driver is not installed: CPU temperatures and motherboard fans are not available.",
            AccessLevel.DriverUnavailable => "The sensor driver is not responding: CPU temperatures and motherboard fans are not available.",
            _ => null,
        };

        if (--_pruneCountdown <= 0)
        {
            _pruneCountdown = 30;
            _index.Prune(hardware);
            foreach (HardwareVM gone in _groups.Keys.Where(k => !hardware.Contains(k)).ToList())
                _groups.Remove(gone);
        }
    }
}
