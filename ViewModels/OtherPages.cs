using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using System.Windows.Input;
using HardwareCenter.Core;

namespace HardwareCenter.ViewModels;

/// <summary>A device heading in the all-sensors list. Clicking it folds the device's sensors away.</summary>
public sealed class SensorDeviceRow(HardwareVM hardware, Action changed) : ObservableObject
{
    private bool _isExpanded = true;
    private ICommand? _toggleCommand;

    public HardwareVM Hardware { get; } = hardware;
    public bool IsChild => Hardware.ParentKey != null;

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (Set(ref _isExpanded, value))
                changed();
        }
    }

    public ICommand ToggleCommand => _toggleCommand ??= new RelayCommand(() => IsExpanded = !IsExpanded);
}

/// <summary>A kind heading ("Temperatures") under a device in the all-sensors list.</summary>
public sealed class SensorKindRow(string title)
{
    public string Title { get; } = title;
}

/// <summary>Every sensor on every device with current, minimum and maximum values: the classic monitor view.</summary>
public sealed class SensorsPageVM : ObservableObject
{
    private const string DetailGroupTitle = "DirectX engines";

    private readonly Dictionary<HardwareVM, SensorDeviceRow> _deviceRows = [];
    private readonly Dictionary<(HardwareVM, string), SensorKindRow> _kindRows = [];
    private MonitorContext? _context;
    private string _filter = "";
    private string _summary = "";
    private bool _isEmpty;

    public SensorsPageVM(Action resetRanges)
    {
        ExpandAllCommand = new RelayCommand(() => SetAllExpanded(true));
        CollapseAllCommand = new RelayCommand(() => SetAllExpanded(false));
        ResetRangesCommand = new RelayCommand(resetRanges);
        ClearFilterCommand = new RelayCommand(() => Filter = "");
    }

    public string Title => "All sensors";

    /// <summary>Device rows, kind rows and sensors in display order. One flat list keeps scrolling virtualized.</summary>
    public ObservableCollection<object> Rows { get; } = [];

    public ICommand ExpandAllCommand { get; }
    public ICommand CollapseAllCommand { get; }
    public ICommand ResetRangesCommand { get; }
    public ICommand ClearFilterCommand { get; }

    public string Filter
    {
        get => _filter;
        set
        {
            if (Set(ref _filter, value ?? ""))
            {
                Raise(nameof(HasFilter));
                Rebuild();
            }
        }
    }

    public bool HasFilter => _filter.Length > 0;
    public string Summary { get => _summary; private set => Set(ref _summary, value); }
    public bool IsEmpty { get => _isEmpty; private set => Set(ref _isEmpty, value); }

    internal void Update(MonitorContext context)
    {
        _context = context;

        // The rows themselves update through their bindings. The list only needs rebuilding when a device
        // or sensor appears or disappears, which is rare, so check for that cheaply first.
        int stamp = StructureStamp(context);
        if (stamp == _structureStamp)
            return;

        _structureStamp = stamp;
        Rebuild();
    }

    private int _structureStamp;

    private static int StructureStamp(MonitorContext context)
    {
        var hash = new HashCode();
        hash.Add(context.Settings.ShowInactiveAdapters);
        foreach (HardwareVM hardware in context.Hardware)
        {
            hash.Add(System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(hardware));
            hash.Add(hardware.Version);
            if (hardware.Kind == HardwareKind.Network)
                hash.Add(Pick.IsActiveNic(hardware));
        }

        return hash.ToHashCode();
    }

    private void SetAllExpanded(bool expanded)
    {
        foreach (SensorDeviceRow row in _deviceRows.Values)
            row.IsExpanded = expanded;
    }

    private void Rebuild()
    {
        if (_context == null)
            return;

        string filter = _filter.Trim();
        bool filtering = filter.Length > 0;
        var rows = new List<object>(512);
        int sensorCount = 0, deviceCount = 0;

        foreach (HardwareVM hardware in _context.Hardware)
        {
            if (hardware.Kind == HardwareKind.Network && !_context.Settings.ShowInactiveAdapters && !Pick.IsActiveNic(hardware))
                continue;

            // A device with nothing to show is still listed when it is a parent (a motherboard and its chips).
            bool isParent = _context.Hardware.Any(h => h.ParentKey == hardware.Key);
            if (hardware.Sensors.Count == 0 && !isParent)
                continue;

            bool deviceMatches = !filtering || Contains(hardware.Name, filter);
            var body = new List<object>();
            int matched = 0;

            foreach (SensorGroupVM group in hardware.Groups)
                matched += AppendGroup(body, hardware, group.Title, group.Sensors, deviceMatches, filter);

            if (hardware.MinorSensors.Count > 0)
                matched += AppendGroup(body, hardware, DetailGroupTitle, hardware.MinorSensors, deviceMatches, filter);

            if (filtering && matched == 0 && !(deviceMatches && isParent))
                continue;

            if (!_deviceRows.TryGetValue(hardware, out SensorDeviceRow? deviceRow))
                _deviceRows[hardware] = deviceRow = new SensorDeviceRow(hardware, Rebuild);

            rows.Add(deviceRow);
            deviceCount++;
            sensorCount += matched;

            // While searching, matches are always shown regardless of what is folded.
            if (deviceRow.IsExpanded || filtering)
                rows.AddRange(body);
        }

        Rows.SyncTo(rows);
        IsEmpty = rows.Count == 0;
        Summary = filtering
            ? $"{sensorCount} matching sensor{(sensorCount == 1 ? "" : "s")}"
            : $"{sensorCount} sensors on {deviceCount} devices";

        if (_deviceRows.Count > _context.Hardware.Count + 8)
            Prune();
    }

    private int AppendGroup(List<object> body, HardwareVM hardware, string title, IEnumerable<SensorVM> sensors, bool deviceMatches, string filter)
    {
        bool groupMatches = deviceMatches || Contains(title, filter);
        List<SensorVM> visible = groupMatches ? sensors.ToList() : sensors.Where(s => Contains(s.Name, filter)).ToList();
        if (visible.Count == 0)
            return 0;

        if (!_kindRows.TryGetValue((hardware, title), out SensorKindRow? kindRow))
            _kindRows[(hardware, title)] = kindRow = new SensorKindRow(title);

        body.Add(kindRow);
        body.AddRange(visible);
        return visible.Count;
    }

    private void Prune()
    {
        var alive = new HashSet<HardwareVM>(_context!.Hardware);
        foreach (HardwareVM gone in _deviceRows.Keys.Where(k => !alive.Contains(k)).ToList())
            _deviceRows.Remove(gone);
        foreach ((HardwareVM, string) gone in _kindRows.Keys.Where(k => !alive.Contains(k.Item1)).ToList())
            _kindRows.Remove(gone);
    }

    private static bool Contains(string text, string filter) =>
        text.Contains(filter, StringComparison.CurrentCultureIgnoreCase);
}

public sealed record InfoBlock(string? Heading, IReadOnlyList<FactRow> Rows)
{
    public bool HasHeading => !string.IsNullOrEmpty(Heading);
}

public sealed record InfoSection(string Title, string IconKey, IReadOnlyList<InfoBlock> Blocks);

/// <summary>The hardware inventory: what is installed in this PC, independent of live readings.</summary>
public sealed class SystemPageVM : ObservableObject
{
    private string _signature = "";
    private object? _lastInventory, _lastDisks, _lastNics;
    private bool _isLoading = true;

    public string Title => "System";
    public ObservableCollection<InfoSection> Sections { get; } = [];
    public bool IsLoading { get => _isLoading; private set => Set(ref _isLoading, value); }

    internal void Update(MonitorContext context, Snapshot? snapshot)
    {
        if (context.Inventory is not { } inventory)
            return;

        // The inputs are immutable and replaced wholesale, so identical references mean nothing changed.
        if (ReferenceEquals(inventory, _lastInventory) && ReferenceEquals(snapshot?.Disks, _lastDisks) && ReferenceEquals(snapshot?.Nics, _lastNics))
            return;

        _lastInventory = inventory;
        _lastDisks = snapshot?.Disks;
        _lastNics = snapshot?.Nics;

        List<InfoSection> sections = Build(inventory, snapshot);

        // The drive and adapter lists are re-read every few seconds; only touch the UI when something changed.
        string signature = Describe(sections);
        if (signature == _signature)
            return;

        _signature = signature;
        Sections.Clear();
        foreach (InfoSection section in sections)
            Sections.Add(section);

        IsLoading = false;
    }

    private static List<InfoSection> Build(Inventory inventory, Snapshot? snapshot)
    {
        SmbiosData smbios = inventory.Smbios;
        var sections = new List<InfoSection>();
        CultureInfo culture = CultureInfo.CurrentCulture;

        sections.Add(Section("Computer", "System", Block(null,
            ("Name", inventory.ComputerName),
            ("Operating system", $"{inventory.OsName} {inventory.OsArchitecture}".Trim()),
            ("Version", inventory.OsVersion),
            ("Installed", inventory.OsInstalled?.ToString("d", culture) ?? ""),
            ("Last started", inventory.LastBoot?.ToString("g", culture) ?? ""))));

        sections.Add(Section("Motherboard", "Board", Block(null,
            ("Manufacturer", smbios.BoardMaker),
            ("Model", smbios.BoardModel),
            ("Revision", smbios.BoardVersion),
            ("BIOS", $"{smbios.BiosVendor} {smbios.BiosVersion}".Trim()),
            ("BIOS date", smbios.BiosDate?.ToString("d", culture) ?? ""))));

        sections.Add(Section("Processor", "Cpu", smbios.Cpus.Select(cpu => Block(
            smbios.Cpus.Count > 1 ? cpu.Name : null,
            ("Model", smbios.Cpus.Count > 1 ? "" : cpu.Name),
            ("Cores / threads", cpu.Cores > 0 ? $"{cpu.Cores} / {cpu.Threads}" : ""),
            ("Socket", cpu.Socket),
            ("Maximum speed", cpu.MaxMhz > 0 ? $"{(cpu.MaxMhz / 1000.0).ToString("0.##", culture)} GHz" : ""),
            ("Cache", string.Join("  ·  ", new[] { Cache("L1", cpu.L1Kb), Cache("L2", cpu.L2Kb), Cache("L3", cpu.L3Kb) }.Where(c => c.Length > 0)))))
            .ToArray()));

        List<SmbiosMemory> modules = smbios.Memory.Where(m => m.SizeMb > 0).ToList();
        var memoryBlocks = new List<InfoBlock>
        {
            Block(null,
                ("Installed", modules.Count > 0 ? $"{modules.Sum(m => m.SizeMb) / 1024.0:0.#} GB" : ""),
                ("Slots used", smbios.Memory.Count > 0 ? $"{modules.Count} of {smbios.Memory.Count}" : "")),
        };
        memoryBlocks.AddRange(modules.Select(m => Block(
            m.Slot.Length > 0 && m.Bank.Length > 0 ? $"{m.Bank} / {m.Slot}" : m.Slot + m.Bank,
            ("Size", $"{m.SizeMb / 1024.0:0.#} GB"),
            ("Type", m.Type + ((m.ConfiguredMhz > 0 ? m.ConfiguredMhz : m.SpeedMhz) > 0 ? $"-{(m.ConfiguredMhz > 0 ? m.ConfiguredMhz : m.SpeedMhz)}" : "")),
            ("Manufacturer", m.Maker),
            ("Part number", m.Part),
            ("Voltage", m.MilliVolts > 0 ? $"{(m.MilliVolts / 1000.0).ToString("0.##", culture)} V" : ""))));
        sections.Add(Section("Memory", "Memory", memoryBlocks.ToArray()));

        sections.Add(Section("Graphics", "Gpu", inventory.Gpus.Select(gpu => Block(
            gpu.Name,
            ("Memory", gpu.VramBytes > 0 ? SensorFormat.Bytes(gpu.VramBytes) : ""),
            ("Driver", gpu.DriverVersion),
            ("Driver date", gpu.DriverDate?.ToString("d", culture) ?? ""),
            ("Current mode", gpu.Width > 0 ? $"{gpu.Width} × {gpu.Height} @ {gpu.RefreshHz} Hz" : "")))
            .ToArray()));

        sections.Add(Section("Displays", "Display", inventory.Displays.Select(display => Block(
            display.Name,
            ("Manufacturer", display.Maker),
            ("Size", display.DiagonalInches > 0 ? $"{display.DiagonalInches.ToString("0.#", culture)} inch" : ""),
            ("Made", display.Year > 0 ? display.Year.ToString(CultureInfo.InvariantCulture) : "")))
            .ToArray()));

        if (snapshot != null)
        {
            sections.Add(Section("Storage", "Storage", snapshot.Disks.Where(d => d.Number >= 0).Select(disk => Block(
                disk.Model,
                ("Type", disk.Kind),
                ("Capacity", SensorFormat.Capacity(disk.SizeBytes)),
                ("Firmware", disk.Firmware),
                ("Volumes", string.Join("   ", disk.Volumes.Select(v => v.Label.Length > 0 ? $"{v.Letter} ({v.Label})" : v.Letter)))))
                .ToArray()));

            sections.Add(Section("Network", "Network", snapshot.Nics
                .Where(n => n.Kind is "Ethernet" or "Wi-Fi" && n.Mac.Length > 0 && !n.Description.Contains("Virtual", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(n => n.IsUp)
                .Select(nic => Block(
                    nic.Description,
                    ("Connection", nic.Name),
                    ("Type", nic.Kind),
                    ("Status", nic.IsUp ? "Connected" : "Disconnected"),
                    ("Link speed", nic.IsUp ? SensorFormat.LinkSpeed(nic.SpeedBitsPerSecond) : ""),
                    ("IPv4 address", nic.IPv4),
                    ("MAC address", nic.Mac)))
                .ToArray()));
        }

        sections.Add(Section("Audio", "Audio", inventory.Audio.Select(a => Block(a.Name, ("Manufacturer", a.Maker))).ToArray()));

        return sections.Where(s => s.Blocks.Count > 0).ToList();
    }

    private static InfoSection Section(string title, string iconKey, params InfoBlock[] blocks) =>
        new(title, iconKey, blocks.Where(b => b.Rows.Count > 0 || b.HasHeading).ToList());

    private static InfoBlock Block(string? heading, params (string Label, string Value)[] rows) =>
        new(heading, rows.Where(r => !string.IsNullOrWhiteSpace(r.Value)).Select(r => new FactRow(r.Label, r.Value)).ToList());

    private static string Cache(string level, long kilobytes) =>
        kilobytes <= 0 ? "" : kilobytes >= 1024 ? $"{level} {kilobytes / 1024.0:0.#} MB" : $"{level} {kilobytes} KB";

    private static string Describe(List<InfoSection> sections)
    {
        var text = new StringBuilder();
        foreach (InfoSection section in sections)
        {
            text.Append(section.Title).Append('\n');
            foreach (InfoBlock block in section.Blocks)
            {
                text.Append(block.Heading).Append('\n');
                foreach (FactRow row in block.Rows)
                    text.Append(row.Label).Append('=').Append(row.Value).Append('\n');
            }
        }

        return text.ToString();
    }
}

public sealed record IntervalOption(string Label, int Milliseconds);

public sealed class SettingsVM : ObservableObject
{
    private readonly AppSettings _settings;
    private readonly Action _applyInterval;
    private readonly Action _applyUnits;
    private string _accessTitle = "";
    private string _accessDetail = "";
    private string? _status;
    private bool _canElevate, _canInstallDriver, _isBusy;

    public SettingsVM(AppSettings settings, Action applyInterval, Action applyUnits, ICommand restartAsAdmin, ICommand installDriver, ICommand exportReport, ICommand resetRanges)
    {
        _settings = settings;
        _applyInterval = applyInterval;
        _applyUnits = applyUnits;
        RestartAsAdminCommand = restartAsAdmin;
        InstallDriverCommand = installDriver;
        ExportReportCommand = exportReport;
        ResetRangesCommand = resetRanges;
        OpenDriverSiteCommand = new RelayCommand(() => Elevation.OpenInBrowser(AccessInfo.DriverHomepage));
    }

    public string Title => "Settings";

    public IReadOnlyList<IntervalOption> Intervals { get; } =
    [
        new("0.5 s", 500),
        new("1 s", 1000),
        new("2 s", 2000),
        new("5 s", 5000),
    ];

    public IntervalOption SelectedInterval
    {
        get => Intervals.FirstOrDefault(i => i.Milliseconds == _settings.IntervalMs) ?? Intervals[1];
        set
        {
            if (value == null || value.Milliseconds == _settings.IntervalMs)
                return;

            _settings.IntervalMs = value.Milliseconds;
            _settings.Save();
            _applyInterval();
            Raise();
        }
    }

    public bool UseCelsius
    {
        get => !_settings.UseFahrenheit;
        set => SetFahrenheit(!value);
    }

    public bool UseFahrenheit
    {
        get => _settings.UseFahrenheit;
        set => SetFahrenheit(value);
    }

    public bool AlwaysRunAsAdministrator
    {
        get => _settings.AlwaysRunAsAdministrator;
        set
        {
            if (_settings.AlwaysRunAsAdministrator == value)
                return;

            _settings.AlwaysRunAsAdministrator = value;
            _settings.Save();
            Raise();
        }
    }

    public bool AlwaysOnTop
    {
        get => _settings.AlwaysOnTop;
        set
        {
            if (_settings.AlwaysOnTop == value)
                return;

            _settings.AlwaysOnTop = value;
            _settings.Save();
            Raise();
        }
    }

    /// <summary>Keep an icon in the notification area and hide the window there when minimized.</summary>
    public bool MinimizeToTray
    {
        get => _settings.MinimizeToTray;
        set
        {
            if (_settings.MinimizeToTray == value)
                return;

            _settings.MinimizeToTray = value;
            _settings.Save();
            Raise();
        }
    }

    /// <summary>Start with Windows, minimized. Stored as a per-user sign-in entry, not in the settings file.</summary>
    public bool StartWithWindows
    {
        get => StartupEntry.IsEnabled;
        set
        {
            if (StartupEntry.IsEnabled != value && !StartupEntry.Set(value))
                Status = "Windows did not allow the start-up entry to be changed.";

            Raise();
        }
    }

    public bool ShowInactiveAdapters
    {
        get => _settings.ShowInactiveAdapters;
        set
        {
            if (_settings.ShowInactiveAdapters == value)
                return;

            _settings.ShowInactiveAdapters = value;
            _settings.Save();
            Raise();
        }
    }

    public ICommand RestartAsAdminCommand { get; }
    public ICommand InstallDriverCommand { get; }
    public ICommand OpenDriverSiteCommand { get; }
    public ICommand ExportReportCommand { get; }
    public ICommand ResetRangesCommand { get; }

    public string AccessTitle { get => _accessTitle; private set => Set(ref _accessTitle, value); }
    public string AccessDetail { get => _accessDetail; private set => Set(ref _accessDetail, value); }
    public bool CanElevate { get => _canElevate; private set => Set(ref _canElevate, value); }
    public bool CanInstallDriver { get => _canInstallDriver; private set => Set(ref _canInstallDriver, value); }
    public bool IsBusy { get => _isBusy; set => Set(ref _isBusy, value); }

    /// <summary>Result of the last action, e.g. where a report was saved.</summary>
    public string? Status { get => _status; set => Set(ref _status, value); }

    public string Version => $"Version {ReportBuilder.AppVersion}";

    internal void ShowAccess(AccessInfo access)
    {
        CanElevate = !access.IsElevated;
        CanInstallDriver = !access.DriverInstalled;

        (AccessTitle, AccessDetail) = access.Level switch
        {
            AccessLevel.Full => (
                "Full sensor access",
                $"Running as administrator with the {AccessInfo.DriverName} driver {access.DriverVersion}. Every sensor this PC exposes is being read."),
            AccessLevel.DriverMissing => (
                "Sensor driver not installed",
                $"Running as administrator, so drive health is available. CPU temperatures, clock speeds and power, and motherboard fans and voltages, are read through the free, signed {AccessInfo.DriverName} driver, which is not installed."),
            AccessLevel.DriverUnavailable => (
                "Sensor driver not responding",
                $"{AccessInfo.DriverName} {access.DriverVersion} is installed but could not be opened. Restarting Windows usually fixes this."),
            _ => (
                "Limited mode",
                "Running without administrator rights. Load, graphics, memory, network and basic drive readings work; CPU temperatures, drive health, and motherboard fans and voltages need administrator rights"
                + (access.DriverInstalled ? "." : $" and the {AccessInfo.DriverName} driver.")),
        };
    }

    private void SetFahrenheit(bool fahrenheit)
    {
        if (_settings.UseFahrenheit == fahrenheit)
            return;

        _settings.UseFahrenheit = fahrenheit;
        _settings.Save();
        _applyUnits();
        Raise(nameof(UseCelsius));
        Raise(nameof(UseFahrenheit));
    }
}
