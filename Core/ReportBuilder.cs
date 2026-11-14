using System.Globalization;
using System.Reflection;
using System.Text;

namespace HardwareCenter.Core;

/// <summary>Plain-text report of the inventory and every sensor, for saving or sharing.</summary>
public static class ReportBuilder
{
    public static string AppVersion =>
        Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0";

    /// <param name="range">Optional lookup of the minimum and maximum seen for a sensor key.</param>
    /// <param name="detailed">Optional low-level report from the sensor library, appended verbatim.</param>
    public static string Build(
        Snapshot snapshot,
        Inventory? inventory,
        AccessInfo access,
        Func<string, (float? Min, float? Max)>? range = null,
        string? detailed = null)
    {
        var report = new StringBuilder(16 * 1024);
        report.AppendLine($"HardwareCenter {AppVersion} report");
        report.AppendLine($"Generated  {DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)}");
        report.AppendLine($"Access     {Describe(access)}");
        report.AppendLine();

        if (inventory != null)
            AppendInventory(report, inventory, snapshot);

        Section(report, "Sensors");
        var children = snapshot.Hardware.Where(h => h.ParentKey != null).ToLookup(h => h.ParentKey!);
        int idleAdapters = 0;
        foreach (HardwareReading hardware in snapshot.Hardware.Where(h => h.ParentKey == null))
        {
            if (IsIdleAdapter(hardware, snapshot))
            {
                idleAdapters++;
                continue;
            }

            AppendHardware(report, hardware, children, range, 0);
        }

        if (idleAdapters > 0)
        {
            report.AppendLine($"({idleAdapters} disconnected network adapters with no traffic are not listed.)");
            report.AppendLine();
        }

        if (!string.IsNullOrWhiteSpace(detailed))
        {
            Section(report, "Low-level report (LibreHardwareMonitorLib)");
            report.AppendLine(detailed.Trim());
        }

        return report.ToString();
    }

    public static string Describe(AccessInfo access) => access.Level switch
    {
        AccessLevel.Full => $"Full (administrator, {AccessInfo.DriverName} {access.DriverVersion})",
        AccessLevel.DriverMissing => $"Administrator, {AccessInfo.DriverName} driver not installed",
        AccessLevel.DriverUnavailable => $"Administrator, {AccessInfo.DriverName} {access.DriverVersion} installed but not responding",
        _ => access.DriverInstalled
            ? $"Limited (not running as administrator; {AccessInfo.DriverName} {access.DriverVersion} installed)"
            : "Limited (not running as administrator)",
    };

    /// <summary>A network adapter that is neither connected nor has carried any data.</summary>
    private static bool IsIdleAdapter(HardwareReading hardware, Snapshot snapshot)
    {
        if (hardware.Kind != HardwareKind.Network)
            return false;

        bool connected = snapshot.Nics.Any(n => n.IsUp && n.Id.Equals(hardware.NetworkId, StringComparison.OrdinalIgnoreCase));
        bool carriedData = hardware.Sensors.Any(s => s.Kind == SensorKind.Data && s.Value > 0);
        return !connected && !carriedData;
    }

    private static void AppendInventory(StringBuilder report, Inventory inventory, Snapshot snapshot)
    {
        SmbiosData smbios = inventory.Smbios;

        Section(report, "System");
        Row(report, "Computer", inventory.ComputerName);
        Row(report, "Operating system", $"{inventory.OsName} {inventory.OsArchitecture}".Trim());
        Row(report, "Version", inventory.OsVersion);
        Row(report, "Motherboard", $"{smbios.BoardMaker} {smbios.BoardModel}".Trim());
        Row(report, "BIOS", $"{smbios.BiosVendor} {smbios.BiosVersion} {smbios.BiosDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}".Trim());

        foreach (SmbiosCpu cpu in smbios.Cpus)
            Row(report, "Processor", $"{cpu.Name} ({cpu.Cores} cores, {cpu.Threads} threads, socket {cpu.Socket})");

        foreach (SmbiosMemory module in smbios.Memory.Where(m => m.SizeMb > 0))
            Row(report, "Memory", $"{$"{module.Bank} {module.Slot}".Trim()}: {module.SizeMb / 1024.0:0.#} GB {module.Type}-{module.ConfiguredMhz} {module.Maker} {module.Part}".Trim());

        foreach (GpuInfo gpu in inventory.Gpus)
            Row(report, "Graphics", $"{gpu.Name} (driver {gpu.DriverVersion}, {SensorFormat.Bytes(gpu.VramBytes)})");

        foreach (DisplayInfo display in inventory.Displays)
            Row(report, "Display", $"{display.Maker} {display.Name}".Trim());

        foreach (DiskInfo disk in snapshot.Disks.Where(d => d.Number >= 0))
            Row(report, "Drive", $"{disk.Model} ({disk.Kind}, {SensorFormat.Capacity(disk.SizeBytes)}) {string.Join(" ", disk.Volumes.Select(v => v.Letter))}".Trim());

        foreach (NicInfo nic in snapshot.Nics.Where(n => n.IsUp && n.Kind is "Ethernet" or "Wi-Fi"))
            Row(report, "Network", $"{nic.Name}: {nic.Description} ({nic.Kind} {SensorFormat.LinkSpeed(nic.SpeedBitsPerSecond)})".Trim());

        foreach (AudioInfo audio in inventory.Audio)
            Row(report, "Audio", audio.Name);

        report.AppendLine();
    }

    private static void AppendHardware(
        StringBuilder report,
        HardwareReading hardware,
        ILookup<string, HardwareReading> children,
        Func<string, (float? Min, float? Max)>? range,
        int depth)
    {
        string indent = new(' ', depth * 2);
        report.AppendLine($"{indent}{hardware.Name}  [{hardware.Kind}]");

        foreach (IGrouping<SensorKind, SensorReading> group in hardware.Sensors
                     .Where(s => s.Value != null)
                     .GroupBy(s => s.Kind)
                     .OrderBy(g => SensorFormat.GroupOrder(g.Key)))
        {
            report.AppendLine($"{indent}  {SensorFormat.GroupTitle(group.Key)}");
            foreach (SensorReading sensor in group.OrderBy(s => s.Index))
            {
                string line = $"{indent}    {sensor.Name,-34} {SensorFormat.Format(sensor.Kind, sensor.Value),14}";
                if (range != null)
                {
                    (float? min, float? max) = range(sensor.Key);
                    if (min != null && max != null)
                        line += $"   min {SensorFormat.Format(sensor.Kind, min),-14} max {SensorFormat.Format(sensor.Kind, max)}";
                }

                report.AppendLine(line.TrimEnd());
            }
        }

        foreach (HardwareReading child in children[hardware.Key])
            AppendHardware(report, child, children, range, depth + 1);

        if (depth == 0)
            report.AppendLine();
    }

    private static void Section(StringBuilder report, string title)
    {
        report.AppendLine(title);
        report.AppendLine(new string('-', Math.Max(title.Length, 40)));
    }

    private static void Row(StringBuilder report, string label, string value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            report.AppendLine($"{label,-18} {value}");
    }
}
