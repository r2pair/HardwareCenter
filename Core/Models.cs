namespace HardwareCenter.Core;

public enum HardwareKind
{
    Motherboard,
    SuperIO,
    Cpu,
    Memory,
    Gpu,
    Storage,
    Network,
    Cooler,
    EmbeddedController,
    Psu,
    Battery,
    PowerMonitor,
    Other,
}

/// <summary>What a sensor measures. The comment is the unit its raw value is reported in.</summary>
public enum SensorKind
{
    Voltage,      // V
    Current,      // A
    Power,        // W
    Clock,        // MHz
    Temperature,  // °C
    Load,         // %
    Frequency,    // Hz
    Fan,          // RPM
    Flow,         // L/h
    Control,      // %
    Level,        // %
    Factor,       // unitless
    Data,         // GB
    SmallData,    // MB
    Throughput,   // B/s
    TimeSpan,     // s
    Timing,       // ns
    Energy,       // mWh
    Noise,        // dBA
    Conductivity, // µS/cm
    Humidity,     // %
}

/// <summary>One sensor value at one point in time.</summary>
/// <param name="Key">Unique within the snapshot.</param>
/// <param name="IsMinor">Detail-level sensor (e.g. per-engine DirectX counters) kept out of summary views.</param>
public sealed record SensorReading(string Key, string Name, SensorKind Kind, int Index, float? Value, bool IsMinor = false);

/// <summary>One device and its sensors at one point in time.</summary>
public sealed record HardwareReading(string Key, string? ParentKey, string Name, HardwareKind Kind, IReadOnlyList<SensorReading> Sensors)
{
    /// <summary>"NVIDIA", "AMD" or "Intel" for graphics adapters.</summary>
    public string? Vendor { get; init; }

    /// <summary>Windows physical drive number for storage devices.</summary>
    public int? DiskNumber { get; init; }

    /// <summary>Adapter GUID for network devices.</summary>
    public string? NetworkId { get; init; }
}

public sealed record VolumeInfo(string Letter, string Label, string FileSystem, long TotalBytes, long FreeBytes)
{
    public long UsedBytes => TotalBytes - FreeBytes;
    public double UsedFraction => TotalBytes > 0 ? (double)UsedBytes / TotalBytes : 0;
}

public sealed record DiskInfo(int Number, string Model, string Kind, long SizeBytes, string Firmware, bool Removable, IReadOnlyList<VolumeInfo> Volumes);

public sealed record NicInfo(string Id, string Name, string Description, string Kind, bool IsUp, long SpeedBitsPerSecond, string Mac, string IPv4);

/// <summary>Everything the monitor thread learned in one update pass.</summary>
public sealed record Snapshot(
    DateTime TimeUtc,
    IReadOnlyList<HardwareReading> Hardware,
    IReadOnlyList<DiskInfo> Disks,
    IReadOnlyList<NicInfo> Nics,
    double UpdateMilliseconds);
