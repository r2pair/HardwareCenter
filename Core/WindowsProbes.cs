using System.Globalization;
using System.IO;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace HardwareCenter.Core;

/// <summary>
/// Physical drives and their volumes, read with query-only IOCTLs that work without elevation.
/// Supplies the facts shown for every drive, and stands in for the sensor library's storage
/// devices (which need administrator rights) when the app runs unelevated.
/// </summary>
internal sealed class DiskProbe
{
    private const int MaxDiskNumber = 31;
    private const double RescanSeconds = 15;
    private const double TemperatureSeconds = 5;
    private const int BusTypeNvme = 17;

    private sealed class DiskState
    {
        public int Number;
        public string Model = "";
        public string Kind = "";
        public string Firmware = "";
        public long SizeBytes;
        public bool Removable;
        public bool Rotational;
        public List<VolumeInfo> Volumes = [];

        public bool HasBaseline;
        public long LastBytesRead, LastBytesWritten, LastIdleTime, LastQueryTime;
        public float? ReadRate, WriteRate, Activity;

        public bool TemperatureSupported = true;
        public DateTime LastTemperatureQueryUtc;
        public float? Temperature;
    }

    private readonly SortedDictionary<int, DiskState> _disks = [];
    private List<VolumeInfo> _unmappedVolumes = [];
    private DateTime _lastScanUtc;

    public IReadOnlyList<DiskInfo> Disks { get; private set; } = [];

    /// <param name="sample">
    /// Whether to read activity and temperature too. Only needed when these drives stand in for the
    /// sensor library; as administrator the library reads them itself and this would be duplicate work.
    /// </param>
    public void Update(DateTime nowUtc, bool sample)
    {
        if ((nowUtc - _lastScanUtc).TotalSeconds >= RescanSeconds)
        {
            _lastScanUtc = nowUtc;
            Rescan();
        }

        if (!sample)
            return;

        foreach (DiskState disk in _disks.Values)
            Sample(disk, nowUtc);
    }

    /// <summary>Adds one storage device per physical drive, using the sensor names the sensor library uses.</summary>
    public void Collect(List<HardwareReading> output)
    {
        foreach (DiskState disk in _disks.Values)
        {
            string key = $"/disk/{disk.Number.ToString(CultureInfo.InvariantCulture)}";
            var sensors = new List<SensorReading>(6);

            if (disk.Temperature != null)
                sensors.Add(Reading(key, "Temperature", SensorKind.Temperature, 0, disk.Temperature));

            long total = disk.Volumes.Sum(v => v.TotalBytes);
            if (total > 0)
            {
                long free = disk.Volumes.Sum(v => v.FreeBytes);
                sensors.Add(Reading(key, "Used Space", SensorKind.Load, 30, (float)(100.0 * (total - free) / total)));
                sensors.Add(Reading(key, "Free Space", SensorKind.Data, 31, (float)(free / 1073741824.0)));
            }

            sensors.Add(Reading(key, "Total Activity", SensorKind.Load, 53, disk.Activity));
            sensors.Add(Reading(key, "Read Rate", SensorKind.Throughput, 54, disk.ReadRate));
            sensors.Add(Reading(key, "Write Rate", SensorKind.Throughput, 55, disk.WriteRate));

            output.Add(new HardwareReading(key, null, disk.Model, HardwareKind.Storage, sensors) { DiskNumber = disk.Number });
        }
    }

    private static SensorReading Reading(string hardwareKey, string name, SensorKind kind, int index, float? value) =>
        new($"{hardwareKey}/{kind.ToString().ToLowerInvariant()}/{index.ToString(CultureInfo.InvariantCulture)}|{name}", name, kind, index, value);

    private void Rescan()
    {
        var present = new HashSet<int>();

        for (int number = 0; number <= MaxDiskNumber; number++)
        {
            using SafeFileHandle handle = Native.OpenForQuery($@"\\.\PhysicalDrive{number}");
            if (handle.IsInvalid)
                continue;

            present.Add(number);
            string model = "", firmware = "";
            int busType = 0;
            bool removable = false;

            byte[]? descriptor = Native.Ioctl(handle, Native.IoctlStorageQueryProperty, PropertyQuery(Native.StorageDeviceProperty), 1024);
            if (descriptor is { Length: >= 36 })
            {
                string vendor = AsciiAt(descriptor, BitConverter.ToInt32(descriptor, 12));
                string product = AsciiAt(descriptor, BitConverter.ToInt32(descriptor, 16));
                firmware = AsciiAt(descriptor, BitConverter.ToInt32(descriptor, 20));
                removable = descriptor[10] != 0;
                busType = BitConverter.ToInt32(descriptor, 28);
                model = product.StartsWith(vendor, StringComparison.OrdinalIgnoreCase) ? product : $"{vendor} {product}".Trim();
            }

            if (model.Length == 0)
                model = $"Disk {number}";

            // No seek penalty means solid-state. Drives that do not answer are assumed rotational.
            byte[]? seek = Native.Ioctl(handle, Native.IoctlStorageQueryProperty, PropertyQuery(Native.StorageDeviceSeekPenaltyProperty), 16);
            bool rotational = seek is not { Length: >= 9 } || seek[8] != 0;

            byte[]? geometry = Native.Ioctl(handle, Native.IoctlDiskGetDriveGeometryEx, null, 256);
            long size = geometry is { Length: >= 32 } ? BitConverter.ToInt64(geometry, 24) : 0;

            if (!_disks.TryGetValue(number, out DiskState? disk) || disk.Model != model)
            {
                disk = new DiskState { Number = number };
                _disks[number] = disk;
            }

            disk.Model = model;
            disk.Firmware = firmware;
            disk.SizeBytes = size;
            disk.Removable = removable;
            disk.Rotational = busType != BusTypeNvme && rotational;
            disk.Kind = DescribeKind(busType, disk.Rotational, removable);
            disk.Volumes = [];
        }

        foreach (int gone in _disks.Keys.Where(n => !present.Contains(n)).ToList())
            _disks.Remove(gone);

        _unmappedVolumes = [];
        foreach ((int? diskNumber, VolumeInfo volume) in ReadVolumes())
        {
            if (diskNumber is int n && _disks.TryGetValue(n, out DiskState? owner))
                owner.Volumes.Add(volume);
            else
                _unmappedVolumes.Add(volume);
        }

        var info = new List<DiskInfo>(_disks.Count + 1);
        foreach (DiskState d in _disks.Values)
            info.Add(new DiskInfo(d.Number, d.Model, d.Kind, d.SizeBytes, d.Firmware, d.Removable, d.Volumes.ToArray()));

        if (_unmappedVolumes.Count > 0)
            info.Add(new DiskInfo(-1, "Other volumes", "Virtual", _unmappedVolumes.Sum(v => v.TotalBytes), "", false, _unmappedVolumes.ToArray()));

        Disks = info;
    }

    private static IEnumerable<(int? DiskNumber, VolumeInfo Volume)> ReadVolumes()
    {
        DriveInfo[] drives;
        try
        {
            drives = DriveInfo.GetDrives();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            yield break;
        }

        foreach (DriveInfo drive in drives)
        {
            // Network and optical drives can block for seconds; they are not this PC's hardware anyway.
            if (drive.DriveType is not (DriveType.Fixed or DriveType.Removable))
                continue;

            VolumeInfo volume;
            string letter = drive.Name.TrimEnd(Path.DirectorySeparatorChar);
            try
            {
                if (!drive.IsReady)
                    continue;

                volume = new VolumeInfo(letter, drive.VolumeLabel ?? "", drive.DriveFormat ?? "", drive.TotalSize, drive.TotalFreeSpace);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            int? diskNumber = null;
            using (SafeFileHandle handle = Native.OpenForQuery($@"\\.\{letter}"))
            {
                if (!handle.IsInvalid)
                {
                    byte[]? extents = Native.Ioctl(handle, Native.IoctlVolumeGetVolumeDiskExtents, null, 256);
                    if (extents is { Length: >= 12 } && BitConverter.ToInt32(extents, 0) > 0)
                        diskNumber = BitConverter.ToInt32(extents, 8);
                }
            }

            yield return (diskNumber, volume);
        }
    }

    private static void Sample(DiskState disk, DateTime nowUtc)
    {
        using SafeFileHandle handle = Native.OpenForQuery($@"\\.\PhysicalDrive{disk.Number}");
        if (handle.IsInvalid)
        {
            disk.ReadRate = disk.WriteRate = disk.Activity = null;
            disk.HasBaseline = false;
            return;
        }

        // DISK_PERFORMANCE: BytesRead @0, BytesWritten @8, IdleTime @32, QueryTime @56 (times in 100 ns units).
        byte[]? perf = Native.Ioctl(handle, Native.IoctlDiskPerformance, null, 96);
        if (perf is { Length: >= 64 })
        {
            long bytesRead = BitConverter.ToInt64(perf, 0);
            long bytesWritten = BitConverter.ToInt64(perf, 8);
            long idleTime = BitConverter.ToInt64(perf, 32);
            long queryTime = BitConverter.ToInt64(perf, 56);

            long elapsed = queryTime - disk.LastQueryTime;
            if (disk.HasBaseline && elapsed > 0)
            {
                double seconds = elapsed / 1e7;
                disk.ReadRate = (float)Math.Max(0, (bytesRead - disk.LastBytesRead) / seconds);
                disk.WriteRate = (float)Math.Max(0, (bytesWritten - disk.LastBytesWritten) / seconds);
                disk.Activity = (float)Math.Clamp(100.0 * (1.0 - (double)(idleTime - disk.LastIdleTime) / elapsed), 0, 100);
            }

            disk.LastBytesRead = bytesRead;
            disk.LastBytesWritten = bytesWritten;
            disk.LastIdleTime = idleTime;
            disk.LastQueryTime = queryTime;
            disk.HasBaseline = true;
        }

        // Asking a spun-down hard disk for its temperature can wake it, so only solid-state drives are polled.
        if (disk.TemperatureSupported && !disk.Rotational && (nowUtc - disk.LastTemperatureQueryUtc).TotalSeconds >= TemperatureSeconds)
        {
            disk.LastTemperatureQueryUtc = nowUtc;

            // STORAGE_TEMPERATURE_DATA_DESCRIPTOR: InfoCount @12, first STORAGE_TEMPERATURE_INFO @24 (Temperature @+2).
            byte[]? data = Native.Ioctl(handle, Native.IoctlStorageQueryProperty, PropertyQuery(Native.StorageDeviceTemperatureProperty), 512);
            if (data is { Length: >= 28 } && BitConverter.ToUInt16(data, 12) > 0)
            {
                short celsius = BitConverter.ToInt16(data, 26);
                disk.Temperature = celsius is > -50 and < 200 ? celsius : null;
            }
            else
            {
                disk.TemperatureSupported = false;
                disk.Temperature = null;
            }
        }
    }

    private static byte[] PropertyQuery(int propertyId)
    {
        // STORAGE_PROPERTY_QUERY { PropertyId, QueryType = PropertyStandardQuery (0), AdditionalParameters[1] }
        byte[] query = new byte[12];
        BitConverter.GetBytes(propertyId).CopyTo(query, 0);
        return query;
    }

    private static string AsciiAt(byte[] buffer, int offset)
    {
        if (offset <= 0 || offset >= buffer.Length)
            return "";

        int end = offset;
        while (end < buffer.Length && buffer[end] != 0)
            end++;

        return Encoding.ASCII.GetString(buffer, offset, end - offset).Trim();
    }

    private static string DescribeKind(int busType, bool rotational, bool removable) => busType switch
    {
        17 => "NVMe SSD",
        11 or 3 => rotational ? "SATA HDD" : "SATA SSD",
        7 => rotational ? "USB drive" : "USB SSD",
        12 or 13 => "Memory card",
        10 => rotational ? "SAS HDD" : "SAS SSD",
        8 => "RAID volume",
        14 or 15 => "Virtual disk",
        16 => "Storage Space",
        _ => removable ? "Removable drive" : rotational ? "Hard disk" : "SSD",
    };
}

/// <summary>
/// Effective CPU speed as Task Manager shows it: Windows' "% Processor Performance" counter applied to
/// the nominal frequency. Used when per-core clocks cannot be read from the processor itself.
/// </summary>
internal sealed class CpuSpeedProbe : IDisposable
{
    private IntPtr _query;
    private readonly IntPtr _performance;
    private readonly IntPtr _frequency;
    private readonly double _registryMhz;

    public CpuSpeedProbe()
    {
        try
        {
            _registryMhz = Convert.ToDouble(
                Registry.GetValue(@"HKEY_LOCAL_MACHINE\HARDWARE\DESCRIPTION\System\CentralProcessor\0", "~MHz", 0) ?? 0,
                CultureInfo.InvariantCulture);
        }
        catch
        {
            _registryMhz = 0;
        }

        if (Native.PdhOpenQueryW(null, IntPtr.Zero, out _query) != 0)
        {
            _query = IntPtr.Zero;
            return;
        }

        if (Native.PdhAddEnglishCounterW(_query, @"\Processor Information(_Total)\% Processor Performance", IntPtr.Zero, out _performance) != 0)
        {
            Dispose();
            return;
        }

        Native.PdhAddEnglishCounterW(_query, @"\Processor Information(_Total)\Processor Frequency", IntPtr.Zero, out _frequency);
        Native.PdhCollectQueryData(_query); // rate counters need two samples
    }

    /// <summary>Average effective speed in MHz, or null when Windows does not provide it.</summary>
    public float? Sample()
    {
        if (_query == IntPtr.Zero || Native.PdhCollectQueryData(_query) != 0)
            return null;

        if (Native.PdhGetFormattedCounterValue(_performance, Native.PdhFmtDouble | Native.PdhFmtNoCap100, out _, out Native.PdhCounterValue performance) != 0
            || performance.Status > 1)
            return null;

        double nominal = _registryMhz;
        if (_frequency != IntPtr.Zero
            && Native.PdhGetFormattedCounterValue(_frequency, Native.PdhFmtDouble, out _, out Native.PdhCounterValue frequency) == 0
            && frequency.Status <= 1 && frequency.DoubleValue > 0)
        {
            nominal = frequency.DoubleValue;
        }

        double mhz = nominal * performance.DoubleValue / 100.0;
        return mhz is > 0 and < 20000 ? (float)mhz : null;
    }

    public void Dispose()
    {
        if (_query != IntPtr.Zero)
        {
            Native.PdhCloseQuery(_query);
            _query = IntPtr.Zero;
        }
    }
}

/// <summary>Recognizes the entries Windows lists beside real adapters.</summary>
internal static partial class NetworkNames
{
    /// <summary>
    /// True for interfaces that are not devices: every filter driver bound to an adapter shows up as its
    /// own interface ("Ethernet-QoS Packet Scheduler-0000"), as do WAN miniports and the kernel debugger.
    /// </summary>
    public static bool IsNotHardware(string name, string? description = null) =>
        FilterInstance().IsMatch(name)
        || name.Contains("Kernel Debugger", StringComparison.OrdinalIgnoreCase)
        || description?.StartsWith("WAN Miniport", StringComparison.OrdinalIgnoreCase) == true
        || description?.Contains("Kernel Debug", StringComparison.OrdinalIgnoreCase) == true;

    [System.Text.RegularExpressions.GeneratedRegex(@"-[^-]+-\d{4}$")]
    private static partial System.Text.RegularExpressions.Regex FilterInstance();
}

/// <summary>Network adapter facts (description, link state, addresses), refreshed occasionally.</summary>
internal sealed class NicCatalog
{
    private const double RefreshSeconds = 10;
    private DateTime _lastRefreshUtc;

    public IReadOnlyList<NicInfo> Nics { get; private set; } = [];

    public void Update(DateTime nowUtc)
    {
        if ((nowUtc - _lastRefreshUtc).TotalSeconds < RefreshSeconds)
            return;

        _lastRefreshUtc = nowUtc;

        try
        {
            var list = new List<NicInfo>();
            foreach (NetworkInterface nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (NetworkNames.IsNotHardware(nic.Name, nic.Description))
                    continue;

                string ipv4 = "";
                try
                {
                    ipv4 = nic.GetIPProperties().UnicastAddresses
                        .FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetwork)?.Address.ToString() ?? "";
                }
                catch (NetworkInformationException)
                {
                    // Adapter went away between enumeration and query.
                }

                byte[] mac = nic.GetPhysicalAddress().GetAddressBytes();
                list.Add(new NicInfo(
                    nic.Id,
                    nic.Name,
                    nic.Description,
                    DescribeKind(nic.NetworkInterfaceType),
                    nic.OperationalStatus == OperationalStatus.Up,
                    nic.Speed,
                    mac.Length > 0 ? string.Join(":", mac.Select(b => b.ToString("X2", CultureInfo.InvariantCulture))) : "",
                    ipv4));
            }

            Nics = list;
        }
        catch (NetworkInformationException)
        {
            // Keep the previous list.
        }
    }

    private static string DescribeKind(NetworkInterfaceType type) => type switch
    {
        NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet or NetworkInterfaceType.FastEthernetT
            or NetworkInterfaceType.FastEthernetFx or NetworkInterfaceType.Ethernet3Megabit => "Ethernet",
        NetworkInterfaceType.Wireless80211 => "Wi-Fi",
        NetworkInterfaceType.Ppp => "PPP",
        NetworkInterfaceType.Tunnel => "Tunnel",
        NetworkInterfaceType.Loopback => "Loopback",
        NetworkInterfaceType.Wwanpp or NetworkInterfaceType.Wwanpp2 => "Cellular",
        _ => "Virtual",
    };
}
