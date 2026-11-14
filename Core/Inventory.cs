using System.Globalization;
using System.IO;
using System.Management;
using System.Text;
using Microsoft.Win32;

namespace HardwareCenter.Core;

public sealed record GpuInfo(string Name, string DriverVersion, DateTime? DriverDate, long VramBytes, int Width, int Height, int RefreshHz);

public sealed record DisplayInfo(string Name, string Maker, int Year, double DiagonalInches);

public sealed record AudioInfo(string Name, string Maker);

/// <summary>Facts about the PC that do not change while the app runs.</summary>
public sealed class Inventory
{
    public string ComputerName { get; init; } = "";
    public string OsName { get; init; } = "";
    public string OsVersion { get; init; } = "";
    public string OsArchitecture { get; init; } = "";
    public DateTime? OsInstalled { get; init; }
    public DateTime? LastBoot { get; init; }
    public SmbiosData Smbios { get; init; } = SmbiosData.Empty;
    public IReadOnlyList<GpuInfo> Gpus { get; init; } = [];
    public IReadOnlyList<DisplayInfo> Displays { get; init; } = [];
    public IReadOnlyList<AudioInfo> Audio { get; init; } = [];

    public long InstalledMemoryMb => Smbios.Memory.Sum(m => m.SizeMb);
}

/// <summary>Gathers the inventory from WMI and the registry. Safe to run on a background thread.</summary>
internal static class InventoryCollector
{
    public static Inventory Collect(SmbiosData smbios)
    {
        string osName = "", osArchitecture = "";
        DateTime? installed = null, lastBoot = null;

        foreach (ManagementBaseObject os in Query(@"root\cimv2", "SELECT Caption, OSArchitecture, InstallDate, LastBootUpTime FROM Win32_OperatingSystem"))
        {
            osName = Text(os, "Caption").Replace("Microsoft ", "", StringComparison.Ordinal);
            osArchitecture = Text(os, "OSArchitecture");
            installed = Date(os, "InstallDate");
            lastBoot = Date(os, "LastBootUpTime");
        }

        return new Inventory
        {
            ComputerName = Environment.MachineName,
            OsName = osName.Length > 0 ? osName : "Windows",
            OsVersion = ReadOsVersion(),
            OsArchitecture = osArchitecture,
            OsInstalled = installed,
            LastBoot = lastBoot,
            Smbios = smbios,
            Gpus = ReadGpus(),
            Displays = ReadDisplays(),
            Audio = ReadAudio(),
        };
    }

    private static string ReadOsVersion()
    {
        try
        {
            using RegistryKey? key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            if (key == null)
                return Environment.OSVersion.Version.ToString();

            string display = key.GetValue("DisplayVersion") as string ?? "";
            string build = key.GetValue("CurrentBuild") as string ?? Environment.OSVersion.Version.Build.ToString(CultureInfo.InvariantCulture);
            string revision = key.GetValue("UBR") is int ubr ? "." + ubr.ToString(CultureInfo.InvariantCulture) : "";
            return display.Length > 0 ? $"{display} (build {build}{revision})" : $"Build {build}{revision}";
        }
        catch
        {
            return Environment.OSVersion.Version.ToString();
        }
    }

    private static List<GpuInfo> ReadGpus()
    {
        Dictionary<string, long> vram = ReadVideoMemoryFromRegistry();
        var gpus = new List<GpuInfo>();

        foreach (ManagementBaseObject gpu in Query(@"root\cimv2",
                     "SELECT Name, DriverVersion, DriverDate, AdapterRAM, CurrentHorizontalResolution, CurrentVerticalResolution, CurrentRefreshRate FROM Win32_VideoController"))
        {
            string name = Text(gpu, "Name");
            if (name.Length == 0)
                continue;

            // AdapterRAM is a 32-bit field that tops out at 4 GB; the driver's registry entry has the real size.
            if (!vram.TryGetValue(name, out long bytes))
                bytes = Number(gpu, "AdapterRAM");

            gpus.Add(new GpuInfo(
                name,
                Text(gpu, "DriverVersion"),
                Date(gpu, "DriverDate"),
                bytes,
                (int)Number(gpu, "CurrentHorizontalResolution"),
                (int)Number(gpu, "CurrentVerticalResolution"),
                (int)Number(gpu, "CurrentRefreshRate")));
        }

        return gpus;
    }

    private static Dictionary<string, long> ReadVideoMemoryFromRegistry()
    {
        var result = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using RegistryKey? displayClass = Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
            if (displayClass == null)
                return result;

            foreach (string subKeyName in displayClass.GetSubKeyNames())
            {
                try
                {
                    using RegistryKey? adapter = displayClass.OpenSubKey(subKeyName);
                    if (adapter?.GetValue("DriverDesc") is not string description)
                        continue;

                    long bytes = adapter.GetValue("HardwareInformation.qwMemorySize") switch
                    {
                        long q => q,
                        int d => (uint)d,
                        byte[] { Length: >= 8 } raw => BitConverter.ToInt64(raw, 0),
                        _ => 0,
                    };

                    if (bytes > 0)
                        result[description] = bytes;
                }
                catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException)
                {
                    // Some subkeys ("Properties") are not readable; they hold nothing we need.
                }
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
        }

        return result;
    }

    private static List<DisplayInfo> ReadDisplays()
    {
        var sizes = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (ManagementBaseObject p in Query(@"root\wmi", "SELECT InstanceName, MaxHorizontalImageSize, MaxVerticalImageSize FROM WmiMonitorBasicDisplayParams"))
        {
            double w = Number(p, "MaxHorizontalImageSize"), h = Number(p, "MaxVerticalImageSize");
            if (w > 0 && h > 0)
                sizes[Text(p, "InstanceName")] = Math.Sqrt(w * w + h * h) / 2.54;
        }

        var displays = new List<DisplayInfo>();
        foreach (ManagementBaseObject m in Query(@"root\wmi", "SELECT InstanceName, UserFriendlyName, ManufacturerName, YearOfManufacture FROM WmiMonitorID"))
        {
            string name = Decode(m["UserFriendlyName"]);
            string makerCode = Decode(m["ManufacturerName"]);
            sizes.TryGetValue(Text(m, "InstanceName"), out double inches);

            displays.Add(new DisplayInfo(
                name.Length > 0 ? name : "Display",
                MakerName(makerCode),
                (int)Number(m, "YearOfManufacture"),
                inches));
        }

        return displays;
    }

    private static List<AudioInfo> ReadAudio()
    {
        var audio = new List<AudioInfo>();
        foreach (ManagementBaseObject device in Query(@"root\cimv2", "SELECT Name, Manufacturer FROM Win32_SoundDevice"))
        {
            string name = Text(device, "Name");
            if (name.Length > 0)
                audio.Add(new AudioInfo(name, Text(device, "Manufacturer").Trim('(', ')')));
        }

        return audio;
    }

    /// <summary>Runs a WMI query, returning nothing if the class is missing or access is denied.</summary>
    private static List<ManagementBaseObject> Query(string scope, string query)
    {
        var results = new List<ManagementBaseObject>();
        try
        {
            using var searcher = new ManagementObjectSearcher(scope, query);
            using ManagementObjectCollection collection = searcher.Get();
            foreach (ManagementBaseObject item in collection)
                results.Add(item);
        }
        catch (Exception ex) when (ex is ManagementException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
        }

        return results;
    }

    private static string Text(ManagementBaseObject item, string property)
    {
        try
        {
            return (item[property] as string)?.Trim() ?? "";
        }
        catch (ManagementException)
        {
            return "";
        }
    }

    private static long Number(ManagementBaseObject item, string property)
    {
        try
        {
            object? value = item[property];
            return value == null ? 0 : Convert.ToInt64(value, CultureInfo.InvariantCulture);
        }
        catch (Exception ex) when (ex is ManagementException or FormatException or InvalidCastException or OverflowException)
        {
            return 0;
        }
    }

    private static DateTime? Date(ManagementBaseObject item, string property)
    {
        try
        {
            return item[property] is string dmtf && dmtf.Length > 0 ? ManagementDateTimeConverter.ToDateTime(dmtf) : null;
        }
        catch (Exception ex) when (ex is ManagementException or ArgumentOutOfRangeException or FormatException)
        {
            return null;
        }
    }

    /// <summary>WMI monitor strings are arrays of UTF-16 code units padded with zeros.</summary>
    private static string Decode(object? value)
    {
        if (value is not ushort[] chars)
            return "";

        var text = new StringBuilder(chars.Length);
        foreach (ushort c in chars)
        {
            if (c == 0)
                break;
            text.Append((char)c);
        }

        return text.ToString().Trim();
    }

    private static string MakerName(string pnpCode) => pnpCode.ToUpperInvariant() switch
    {
        "ACR" => "Acer",
        "AOC" => "AOC",
        "APP" => "Apple",
        "AUO" => "AU Optronics",
        "AUS" => "ASUS",
        "BNQ" => "BenQ",
        "BOE" => "BOE",
        "CMN" => "Chi Mei Innolux",
        "DEL" => "Dell",
        "GBT" => "Gigabyte",
        "GSM" => "LG",
        "HPN" or "HWP" => "HP",
        "IVM" => "iiyama",
        "LEN" => "Lenovo",
        "LGD" => "LG Display",
        "MSI" => "MSI",
        "NEC" => "NEC",
        "PHL" => "Philips",
        "SAM" or "SEC" => "Samsung",
        "SDC" => "Samsung Display",
        "SHP" => "Sharp",
        "SNY" => "Sony",
        "VSC" => "ViewSonic",
        "" => "",
        _ => pnpCode,
    };
}
