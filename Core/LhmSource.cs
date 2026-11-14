using LibreHardwareMonitor.Hardware;

namespace HardwareCenter.Core;

public sealed record SmbiosCpu(string Name, string Socket, int Cores, int Threads, int MaxMhz, long L1Kb, long L2Kb, long L3Kb);

public sealed record SmbiosMemory(string Slot, string Bank, long SizeMb, string Type, int SpeedMhz, int ConfiguredMhz, string Maker, string Part, int MilliVolts);

public sealed record SmbiosData(
    string BoardMaker, string BoardModel, string BoardVersion,
    string BiosVendor, string BiosVersion, DateTime? BiosDate,
    IReadOnlyList<SmbiosCpu> Cpus, IReadOnlyList<SmbiosMemory> Memory)
{
    public static readonly SmbiosData Empty = new("", "", "", "", "", null, [], []);
}

/// <summary>
/// Reads sensors through LibreHardwareMonitorLib and converts them to this app's own model, so nothing
/// outside this file depends on the library's types. All members must be called from one thread.
/// </summary>
internal sealed class LhmSource : IDisposable
{
    private readonly Computer _computer = new()
    {
        IsCpuEnabled = true,
        IsGpuEnabled = true,
        IsMemoryEnabled = true,
        IsMotherboardEnabled = true,
        IsControllerEnabled = true,
        IsNetworkEnabled = true,
        IsStorageEnabled = true,
        IsBatteryEnabled = true,
        IsPsuEnabled = true,
        IsPowerMonitorEnabled = true,
    };

    private readonly UpdateVisitor _visitor = new();
    private bool _open;

    public void Open()
    {
        _computer.Open();
        _open = true;
        CloseEmptyGroups();
    }

    /// <summary>
    /// Stops looking for kinds of device this PC turned out not to have (a battery on a desktop, USB fan
    /// controllers, PSU links), so their scanners hold no handles and are never polled.
    /// </summary>
    private void CloseEmptyGroups()
    {
        try
        {
            var found = new HashSet<HardwareType>(_computer.Hardware.Select(h => h.HardwareType));
            if (!found.Contains(HardwareType.Battery))
                _computer.IsBatteryEnabled = false;
            if (!found.Contains(HardwareType.Psu))
                _computer.IsPsuEnabled = false;
            if (!found.Contains(HardwareType.PowerMonitor))
                _computer.IsPowerMonitorEnabled = false;
            if (!found.Contains(HardwareType.Cooler))
                _computer.IsControllerEnabled = false;
        }
        catch
        {
            // Leaving an empty group open only costs a little; it is never worth failing startup over.
        }
    }

    public void Update()
    {
        if (_open)
            _computer.Accept(_visitor);
    }

    // What never changes about a device or sensor is worked out once and reused on every pass;
    // building the identifier strings alone was a large share of the per-second garbage.
    private sealed record HardwareFacts(string Key, HardwareKind Kind, string? Vendor, int? DiskNumber, string? NetworkId);

    private sealed record SensorFacts(string Key, SensorKind Kind, bool IsMinor, bool IsFrameRate);

    private readonly Dictionary<IHardware, HardwareFacts> _hardwareFacts = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<ISensor, SensorFacts> _sensorFacts = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<string> _assignedKeys = new(StringComparer.Ordinal);
    private int _sensorsSeen;

    /// <param name="driverReady">
    /// False when the kernel driver cannot be used. The library then reports placeholder values for
    /// every CPU sensor except load (0 °C, 0 W, 1.55 V), so those are dropped rather than shown.
    /// </param>
    public void Collect(List<HardwareReading> output, bool driverReady)
    {
        if (!_open)
            return;

        _sensorsSeen = 0;
        foreach (IHardware hardware in _computer.Hardware)
            Collect(hardware, null, output, driverReady);

        // Devices come and go (USB drives); once the caches hold far more than is live, start afresh.
        if (_sensorFacts.Count > _sensorsSeen * 2 + 64)
        {
            _hardwareFacts.Clear();
            _sensorFacts.Clear();
            _assignedKeys.Clear();
        }
    }

    public string GetDetailedReport()
    {
        try
        {
            return _open ? _computer.GetReport() : string.Empty;
        }
        catch (Exception ex)
        {
            return "Detailed report unavailable: " + ex.Message;
        }
    }

    public SmbiosData ReadSmbios()
    {
        if (!_open)
            return SmbiosData.Empty;

        try
        {
            SMBios smbios = _computer.SMBios;

            var caches = new Dictionary<ushort, long>();
            foreach (CacheInformation cache in smbios.ProcessorCaches ?? [])
                caches[cache.Handle] = DecodeCacheSize(cache.Size);

            var cpus = new List<SmbiosCpu>();
            foreach (ProcessorInformation p in smbios.Processors ?? [])
            {
                if (p.CoreCount == 0 && string.IsNullOrWhiteSpace(p.Version))
                    continue; // unpopulated socket

                cpus.Add(new SmbiosCpu(
                    Clean(p.Version),
                    Clean(p.SocketDesignation),
                    p.CoreCount,
                    p.ThreadCount,
                    p.MaxSpeed,
                    caches.GetValueOrDefault(p.L1CacheHandle),
                    caches.GetValueOrDefault(p.L2CacheHandle),
                    caches.GetValueOrDefault(p.L3CacheHandle)));
            }

            var memory = new List<SmbiosMemory>();
            foreach (MemoryDevice m in smbios.MemoryDevices ?? [])
            {
                memory.Add(new SmbiosMemory(
                    Clean(m.DeviceLocator),
                    Clean(m.BankLocator),
                    m.Size,
                    m.Size > 0 ? m.Type.ToString() : "",
                    m.Speed,
                    m.ConfiguredSpeed,
                    Clean(m.ManufacturerName),
                    Clean(m.PartNumber),
                    m.ConfiguredVoltage));
            }

            return new SmbiosData(
                Clean(smbios.Board?.ManufacturerName),
                Clean(smbios.Board?.ProductName),
                Clean(smbios.Board?.Version),
                Clean(smbios.Bios?.Vendor),
                Clean(smbios.Bios?.Version),
                smbios.Bios?.Date,
                cpus,
                memory);
        }
        catch
        {
            return SmbiosData.Empty;
        }
    }

    public void Dispose()
    {
        if (!_open)
            return;

        _open = false;
        try
        {
            _computer.Close();
        }
        catch
        {
            // Shutting down: a device that fails to close cleanly is not actionable.
        }
    }

    /// <summary>SMBIOS stores cache size in 1 KB units, or 64 KB units when the top bit is set.</summary>
    private static long DecodeCacheSize(ushort raw) =>
        (raw & 0x8000) != 0 ? (raw & 0x7FFF) * 64L : raw;

    private static string Clean(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "";

        string trimmed = value.Trim();
        return trimmed is "Unknown" or "Default string" or "To Be Filled By O.E.M." or "Not Specified" or "x.x" or "None"
            ? ""
            : trimmed;
    }

    private void Collect(IHardware hardware, string? parentKey, List<HardwareReading> output, bool driverReady)
    {
        string name = hardware.Name ?? "Unknown device";

        if (!_hardwareFacts.TryGetValue(hardware, out HardwareFacts? facts))
        {
            HardwareKind hardwareKind = MapKind(hardware.HardwareType);
            string hardwareKey = Unique(hardware.Identifier.ToString());
            facts = new HardwareFacts(
                hardwareKey,
                hardwareKind,
                hardware.HardwareType switch
                {
                    HardwareType.GpuNvidia => "NVIDIA",
                    HardwareType.GpuAmd => "AMD",
                    HardwareType.GpuIntel => "Intel",
                    _ => null,
                },
                hardwareKind == HardwareKind.Storage ? LastSegmentAsNumber(hardwareKey) : null,
                hardwareKind == HardwareKind.Network ? Uri.UnescapeDataString(LastSegment(hardwareKey)) : null);
            _hardwareFacts.Add(hardware, facts);
        }

        if (facts.Kind == HardwareKind.Network && NetworkNames.IsNotHardware(name))
            return;

        ISensor[] sensors;
        try
        {
            sensors = hardware.Sensors;
        }
        catch
        {
            sensors = [];
        }

        var readings = new List<SensorReading>(sensors.Length);
        foreach (ISensor sensor in sensors)
        {
            if (!_sensorFacts.TryGetValue(sensor, out SensorFacts? sensorFacts))
            {
                SensorKind sensorKind = MapKind(sensor.SensorType);
                string initialName = sensor.Name ?? sensorKind.ToString();

                // Sensor identifiers alone are not unique (two NVIDIA load sensors share an index),
                // so the name is part of the key.
                sensorFacts = new SensorFacts(
                    Unique($"{sensor.Identifier}|{initialName}"),
                    sensorKind,
                    facts.Kind == HardwareKind.Gpu && initialName.StartsWith("D3D ", StringComparison.Ordinal),
                    initialName.Contains("FPS", StringComparison.OrdinalIgnoreCase));
                _sensorFacts.Add(sensor, sensorFacts);
            }

            _sensorsSeen++;
            if (facts.Kind == HardwareKind.Cpu && !driverReady && sensorFacts.Kind != SensorKind.Load)
                continue;

            float? value = sensor.Value;
            if (value is float v && (float.IsNaN(v) || float.IsInfinity(v)))
                value = null;

            // The library reports -1 while no fullscreen application is running.
            if (value < 0 && sensorFacts.IsFrameRate)
                value = null;

            readings.Add(new SensorReading(
                sensorFacts.Key, sensor.Name ?? sensorFacts.Kind.ToString(), sensorFacts.Kind, sensor.Index, value, sensorFacts.IsMinor));
        }

        output.Add(new HardwareReading(facts.Key, parentKey, name, facts.Kind, readings)
        {
            Vendor = facts.Vendor,
            DiskNumber = facts.DiskNumber,
            NetworkId = facts.NetworkId,
        });

        IHardware[] children;
        try
        {
            children = hardware.SubHardware;
        }
        catch
        {
            children = [];
        }

        foreach (IHardware child in children)
            Collect(child, facts.Key, output, driverReady);
    }

    private string Unique(string key)
    {
        if (_assignedKeys.Add(key))
            return key;

        for (int n = 2; ; n++)
        {
            string candidate = $"{key}#{n}";
            if (_assignedKeys.Add(candidate))
                return candidate;
        }
    }

    private static string LastSegment(string key)
    {
        int slash = key.LastIndexOf('/');
        return slash >= 0 ? key[(slash + 1)..] : key;
    }

    private static int? LastSegmentAsNumber(string key) =>
        int.TryParse(LastSegment(key), out int number) ? number : null;

    private static HardwareKind MapKind(HardwareType type) => type switch
    {
        HardwareType.Motherboard => HardwareKind.Motherboard,
        HardwareType.SuperIO => HardwareKind.SuperIO,
        HardwareType.Cpu => HardwareKind.Cpu,
        HardwareType.Memory => HardwareKind.Memory,
        HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel => HardwareKind.Gpu,
        HardwareType.Storage => HardwareKind.Storage,
        HardwareType.Network => HardwareKind.Network,
        HardwareType.Cooler => HardwareKind.Cooler,
        HardwareType.EmbeddedController => HardwareKind.EmbeddedController,
        HardwareType.Psu => HardwareKind.Psu,
        HardwareType.Battery => HardwareKind.Battery,
        HardwareType.PowerMonitor => HardwareKind.PowerMonitor,
        _ => HardwareKind.Other,
    };

    private static SensorKind MapKind(SensorType type) => type switch
    {
        SensorType.Voltage => SensorKind.Voltage,
        SensorType.Current => SensorKind.Current,
        SensorType.Power => SensorKind.Power,
        SensorType.Clock => SensorKind.Clock,
        SensorType.Temperature => SensorKind.Temperature,
        SensorType.Load => SensorKind.Load,
        SensorType.Frequency => SensorKind.Frequency,
        SensorType.Fan => SensorKind.Fan,
        SensorType.Flow => SensorKind.Flow,
        SensorType.Control => SensorKind.Control,
        SensorType.Level => SensorKind.Level,
        SensorType.Factor => SensorKind.Factor,
        SensorType.Data => SensorKind.Data,
        SensorType.SmallData => SensorKind.SmallData,
        SensorType.Throughput => SensorKind.Throughput,
        SensorType.TimeSpan => SensorKind.TimeSpan,
        SensorType.Timing => SensorKind.Timing,
        SensorType.Energy => SensorKind.Energy,
        SensorType.Noise => SensorKind.Noise,
        SensorType.Conductivity => SensorKind.Conductivity,
        SensorType.Humidity => SensorKind.Humidity,
        _ => SensorKind.Factor,
    };

    private sealed class UpdateVisitor : IVisitor
    {
        public void VisitComputer(IComputer computer) => computer.Traverse(this);

        public void VisitHardware(IHardware hardware)
        {
            try
            {
                hardware.Update();
            }
            catch
            {
                // One misbehaving device must not stop every other device from updating.
            }

            foreach (IHardware child in hardware.SubHardware)
                child.Accept(this);
        }

        public void VisitSensor(ISensor sensor) { }

        public void VisitParameter(IParameter parameter) { }
    }
}
