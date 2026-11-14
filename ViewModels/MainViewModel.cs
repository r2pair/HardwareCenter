using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using HardwareCenter.Core;
using Microsoft.Win32;

namespace HardwareCenter.ViewModels;

public sealed class NavItem(string title, string iconKey, object page) : ObservableObject
{
    private string _badge = "";
    private bool _isVisible = true;

    public string Title { get; } = title;
    public string IconKey { get; } = iconKey;
    public object Page { get; } = page;

    /// <summary>A live figure shown beside the title, e.g. the CPU load.</summary>
    public string Badge { get => _badge; internal set => Set(ref _badge, value); }

    public bool IsVisible { get => _isVisible; internal set => Set(ref _isVisible, value); }
}

/// <summary>The strip that explains reduced sensor access and offers the one action that fixes it.</summary>
public sealed class AccessBannerVM : ObservableObject
{
    private bool _isVisible;
    private string _title = "";
    private string _message = "";
    private string? _actionText;
    private ICommand? _action;
    private bool _isBusy;

    public AccessBannerVM()
    {
        DismissCommand = new RelayCommand(() => IsVisible = false);
    }

    public ICommand DismissCommand { get; }
    public bool IsVisible { get => _isVisible; private set => Set(ref _isVisible, value); }
    public string Title { get => _title; private set => Set(ref _title, value); }
    public string Message { get => _message; private set => Set(ref _message, value); }
    public ICommand? Action { get => _action; private set => Set(ref _action, value); }
    public bool IsBusy { get => _isBusy; set => Set(ref _isBusy, value); }

    public string? ActionText
    {
        get => _actionText;
        private set
        {
            if (Set(ref _actionText, value))
                Raise(nameof(HasAction));
        }
    }

    public bool HasAction => _actionText != null;

    public void Show(string title, string message, string? actionText = null, ICommand? action = null)
    {
        Title = title;
        Message = message;
        ActionText = actionText;
        Action = action;
        IsVisible = true;
    }

    public void Hide() => IsVisible = false;
}

public sealed class MainViewModel : ObservableObject, IDisposable
{
    private const int RemoveHardwareAfterMissedTicks = 5;

    private readonly Dispatcher _dispatcher;
    private readonly MonitorEngine _engine = new();
    private readonly Dictionary<string, HardwareVM> _byKey = new(StringComparer.Ordinal);
    private List<HardwareVM> _hardware = [];
    private Snapshot? _pending;
    private Snapshot? _latest;
    private NavItem _selectedNav;
    private bool _isLoading = true;
    private string _accessLabel = "Starting…";
    private Brush _accessBrush = Palette.Brush(Palette.Slate);
    private string? _startupError;
    private bool _disposed;

    public MainViewModel(AppSettings settings)
    {
        _dispatcher = Dispatcher.CurrentDispatcher;
        Settings = settings;
        Context = new MonitorContext(settings);
        SensorFormat.UseFahrenheit = settings.UseFahrenheit;

        RestartAsAdminCommand = new RelayCommand(RestartAsAdministrator);
        InstallDriverCommand = new RelayCommand(() => _ = InstallDriverAsync());
        RestartCommand = new RelayCommand(RestartNow);
        ExportReportCommand = new RelayCommand(ExportReport);
        ResetRangesCommand = new RelayCommand(ResetRanges);

        Dashboard = new DashboardVM();
        Thermals = new ThermalsPageVM();
        Sensors = new SensorsPageVM(ResetRanges);
        SystemInfo = new SystemPageVM();
        SettingsPage = new SettingsVM(settings, ApplyInterval, ApplyUnits, RestartAsAdminCommand, InstallDriverCommand, ExportReportCommand, ResetRangesCommand);

        CpuPage = new DevicePageVM("Processor", "No processor sensors were found.", (h, _) => h.Kind == HardwareKind.Cpu);
        GpuPage = new DevicePageVM("Graphics", "No graphics adapters were found.", (h, _) => h.Kind == HardwareKind.Gpu,
            list => list.OrderByDescending(h => Pick.GpuMemory(h).Total?.Value ?? 0));
        MemoryPage = new DevicePageVM("Memory", "No memory sensors were found.", (h, _) => h.Kind == HardwareKind.Memory,
            list => list.OrderBy(h => h.Key == "/ram" ? 0 : h.Key == "/vram" ? 2 : 1));
        StoragePage = new DevicePageVM("Storage", "No drives were found.", (h, _) => h.Kind == HardwareKind.Storage);
        NetworkPage = new DevicePageVM("Network", "No active network adapters. Inactive adapters can be shown from Settings.",
            (h, c) => h.Kind == HardwareKind.Network && (c.Settings.ShowInactiveAdapters || Pick.IsActiveNic(h)),
            list => list.OrderByDescending(h => h.Nic?.IsUp == true).ThenByDescending(Pick.NetworkTotalData));
        BoardPage = new DevicePageVM("Motherboard", "No motherboard information was found.",
            (h, _) => h.Kind is HardwareKind.Motherboard or HardwareKind.SuperIO or HardwareKind.EmbeddedController
                or HardwareKind.Cooler or HardwareKind.Psu or HardwareKind.Battery or HardwareKind.PowerMonitor or HardwareKind.Other);

        _cpuNav = new NavItem("Processor", "Cpu", CpuPage);
        _gpuNav = new NavItem("Graphics", "Gpu", GpuPage);
        _memoryNav = new NavItem("Memory", "Memory", MemoryPage);

        Nav =
        [
            new NavItem("Overview", "Dashboard", Dashboard),
            _cpuNav,
            _gpuNav,
            _memoryNav,
            new NavItem("Storage", "Storage", StoragePage),
            new NavItem("Network", "Network", NetworkPage),
            new NavItem("Motherboard", "Board", BoardPage),
            new NavItem("Thermals", "Thermometer", Thermals),
            new NavItem("All sensors", "Activity", Sensors),
            new NavItem("System", "System", SystemInfo),
        ];
        SettingsNav = new NavItem("Settings", "Settings", SettingsPage);
        _selectedNav = Nav[0];

        _engine.IntervalMs = settings.IntervalMs;
        _engine.AccessDetected += access => _dispatcher.BeginInvoke(() => OnAccessDetected(access));
        _engine.InventoryReady += inventory => _dispatcher.BeginInvoke(() => OnInventoryReady(inventory));
        _engine.SnapshotReady += OnSnapshot;

        Banner.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(AccessBannerVM.IsVisible))
                Raise(nameof(ShowBanner));
        };
    }

    private readonly NavItem _cpuNav, _gpuNav, _memoryNav;

    public AppSettings Settings { get; }
    public MonitorContext Context { get; }

    public DashboardVM Dashboard { get; }
    public DevicePageVM CpuPage { get; }
    public DevicePageVM GpuPage { get; }
    public DevicePageVM MemoryPage { get; }
    public DevicePageVM StoragePage { get; }
    public DevicePageVM NetworkPage { get; }
    public DevicePageVM BoardPage { get; }
    public ThermalsPageVM Thermals { get; }
    public SensorsPageVM Sensors { get; }
    public SystemPageVM SystemInfo { get; }
    public SettingsVM SettingsPage { get; }
    public AccessBannerVM Banner { get; } = new();

    /// <summary>The access banner belongs to the overview; other pages keep their full height.</summary>
    public bool ShowBanner => Banner.IsVisible && ReferenceEquals(CurrentPage, Dashboard);

    /// <summary>Tooltip for the notification-area icon: the two figures people glance at.</summary>
    public string TrayText { get => _trayText; private set => Set(ref _trayText, value); }

    private string _trayText = "HardwareCenter";

    public ObservableCollection<NavItem> Nav { get; }
    public NavItem SettingsNav { get; }

    public ICommand RestartAsAdminCommand { get; }
    public ICommand InstallDriverCommand { get; }
    public ICommand RestartCommand { get; }
    public ICommand ExportReportCommand { get; }
    public ICommand ResetRangesCommand { get; }

    public NavItem SelectedNav
    {
        get => _selectedNav;
        set
        {
            if (value == null || !Set(ref _selectedNav, value))
                return;

            Raise(nameof(SelectedListItem));
            Raise(nameof(ShowBanner));
            Raise(nameof(CurrentPage));
            Raise(nameof(CurrentTitle));
            Raise(nameof(IsSettingsSelected));
            UpdatePage(value.Page);
        }
    }

    /// <summary>
    /// Selection as the navigation list sees it: null while Settings (which sits outside the list)
    /// is showing, so the list drops its highlight.
    /// </summary>
    public NavItem? SelectedListItem
    {
        get => ReferenceEquals(_selectedNav, SettingsNav) ? null : _selectedNav;
        set
        {
            if (value != null)
                SelectedNav = value;
        }
    }

    public object CurrentPage => _selectedNav.Page;
    public string CurrentTitle => _selectedNav.Title;

    public bool IsSettingsSelected
    {
        get => ReferenceEquals(_selectedNav, SettingsNav);
        set
        {
            if (value)
                SelectedNav = SettingsNav;
        }
    }

    /// <summary>True until the first readings arrive.</summary>
    public bool IsLoading { get => _isLoading; private set => Set(ref _isLoading, value); }

    public string AccessLabel { get => _accessLabel; private set => Set(ref _accessLabel, value); }
    public Brush AccessBrush { get => _accessBrush; private set => Set(ref _accessBrush, value); }
    public string? StartupError { get => _startupError; private set => Set(ref _startupError, value); }

    public bool AlwaysOnTop => Settings.AlwaysOnTop;

    /// <summary>The running version, shown beside the app name.</summary>
    public string VersionText => "v" + ReportBuilder.AppVersion;

    public void Start() => _engine.Start();

    /// <summary>Selects a page by its navigation title, ignoring case. Unknown names are ignored.</summary>
    public void SelectPage(string title)
    {
        NavItem? match = Nav.Append(SettingsNav).FirstOrDefault(n => n.Title.Equals(title, StringComparison.OrdinalIgnoreCase));
        if (match != null)
            SelectedNav = match;
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _engine.SnapshotReady -= OnSnapshot;
        _engine.Dispose();
    }

    // ---- Engine events ----

    /// <summary>Called on the monitor thread. Only the newest unprocessed snapshot is kept.</summary>
    private void OnSnapshot(Snapshot snapshot)
    {
        if (Interlocked.Exchange(ref _pending, snapshot) == null)
            _dispatcher.BeginInvoke(DispatcherPriority.Normal, ApplyPending);
    }

    private void ApplyPending()
    {
        Snapshot? snapshot = Interlocked.Exchange(ref _pending, null);
        if (snapshot != null && !_disposed)
            Apply(snapshot);
    }

    private void OnAccessDetected(AccessInfo access)
    {
        Context.Access = access;
        SettingsPage.ShowAccess(access);

        (AccessLabel, Color color) = access.Level switch
        {
            AccessLevel.Full => ("Full sensor access", Palette.Good),
            AccessLevel.DriverMissing => ("Sensor driver missing", Palette.Warning),
            AccessLevel.DriverUnavailable => ("Sensor driver not responding", Palette.Warning),
            _ => ("Limited mode", Palette.Warning),
        };
        AccessBrush = Palette.Brush(color);

        switch (access.Level)
        {
            case AccessLevel.Limited:
                Banner.Show(
                    "Limited mode",
                    "CPU temperatures, drive health, and motherboard fans and voltages need administrator rights.",
                    "Restart as administrator", RestartAsAdminCommand);
                break;
            case AccessLevel.DriverMissing:
                Banner.Show(
                    "Sensor driver not installed",
                    $"CPU temperatures, clock speeds, fan speeds and voltages are read through the free {AccessInfo.DriverName} driver.",
                    $"Install {AccessInfo.DriverName}", InstallDriverCommand);
                break;
            case AccessLevel.DriverUnavailable:
                Banner.Show(
                    "Sensor driver not responding",
                    $"{AccessInfo.DriverName} is installed but could not be opened. Restarting Windows usually fixes this.");
                break;
            default:
                Banner.Hide();
                break;
        }
    }

    private void OnInventoryReady(Inventory inventory)
    {
        Context.Inventory = inventory;
        RefreshPages();
    }

    private void Apply(Snapshot snapshot)
    {
        long ticks = snapshot.TimeUtc.Ticks;
        _latest = snapshot;

        foreach (HardwareVM hardware in _hardware)
            hardware.MissedTicks++;

        var current = new List<HardwareVM>(snapshot.Hardware.Count);
        foreach (HardwareReading reading in snapshot.Hardware)
        {
            if (!_byKey.TryGetValue(reading.Key, out HardwareVM? hardware))
                _byKey[reading.Key] = hardware = new HardwareVM(reading);

            hardware.MissedTicks = 0;
            hardware.Apply(reading, ticks, _isInBackground);

            if (hardware.DiskNumber is int disk)
                hardware.Disk = snapshot.Disks.FirstOrDefault(d => d.Number == disk);
            if (hardware.NetworkId is string id)
                hardware.Nic = snapshot.Nics.FirstOrDefault(n => n.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

            current.Add(hardware);
        }

        // A device that skips a pass (a USB drive waking up) keeps its place for a few seconds before it is dropped.
        foreach (HardwareVM hardware in _hardware)
        {
            if (hardware.MissedTicks == 0)
                continue;

            if (hardware.MissedTicks < RemoveHardwareAfterMissedTicks)
                current.Add(hardware);
            else
                _byKey.Remove(hardware.Key);
        }

        _hardware = current;
        TrayText = DescribeForTray(current);
        Context.Hardware = current;

        // Sensors above keep recording history and min/max; the pages themselves only matter when visible.
        if (!_isInBackground)
            RefreshPages();

        if (IsLoading)
        {
            IsLoading = false;
            StartupError = _engine.InitializationError == null
                ? null
                : $"The sensor library could not start ({_engine.InitializationError}). Showing Windows-level readings only.";
        }
    }

    /// <summary>The overview feeds the navigation badges, so it is always current; other pages update while shown.</summary>
    private void RefreshPages()
    {
        Dashboard.Update(Context);
        _cpuNav.Badge = Badge(Dashboard.Cpu.Primary);
        _gpuNav.Badge = Badge(Dashboard.Gpu.Primary);
        _memoryNav.Badge = Badge(Dashboard.Memory.Primary);

        if (!ReferenceEquals(CurrentPage, Dashboard))
            UpdatePage(CurrentPage);
    }

    private static string DescribeForTray(List<HardwareVM> hardware)
    {
        var text = new System.Text.StringBuilder("HardwareCenter");

        if (hardware.FirstOrDefault(h => h.Kind == HardwareKind.Cpu) is { } cpu)
            text.Append('\n').Append("CPU  ").Append(Describe(Pick.CpuLoad(cpu), Pick.CpuTemperature(cpu)));

        if (Pick.PrimaryGpu(hardware) is { } gpu)
            text.Append('\n').Append("GPU  ").Append(Describe(Pick.GpuLoad(gpu), Pick.GpuTemperature(gpu)));

        return text.ToString();

        static string Describe(SensorVM? load, SensorVM? temperature)
        {
            string result = SensorFormat.FormatCompact(SensorKind.Load, load?.Value);
            return temperature?.Value is float degrees
                ? $"{result}   {SensorFormat.FormatCompact(SensorKind.Temperature, degrees)}"
                : result;
        }
    }

    private static string Badge(StatVM stat) => stat.Text == SensorFormat.Missing ? "" : stat.Text + stat.Unit;

    private void UpdatePage(object page)
    {
        switch (page)
        {
            case DevicePageVM devices:
                devices.Update(Context);
                break;
            case ThermalsPageVM thermals:
                thermals.Update(Context);
                break;
            case SensorsPageVM sensors:
                sensors.Update(Context);
                break;
            case SystemPageVM system:
                system.Update(Context, _latest);
                break;
        }
    }

    // ---- Settings ----

    /// <summary>How often sensors are read while the window is minimized.</summary>
    private const int BackgroundIntervalMs = 5000;

    private bool _isInBackground;

    /// <summary>
    /// Call when the window is minimized or restored. In the background sensors are read slowly, enough
    /// to keep history and min/max filling in, and no page is updated.
    /// </summary>
    public void SetBackground(bool inBackground)
    {
        if (_isInBackground == inBackground)
            return;

        _isInBackground = inBackground;
        ApplyInterval();

        if (!inBackground && !IsLoading)
        {
            // Values recorded quietly while minimized now need their on-screen text and meters brought up to date.
            foreach (HardwareVM hardware in _hardware)
                hardware.RefreshFormatting();

            RefreshPages();
        }
    }

    private void ApplyInterval() =>
        _engine.IntervalMs = _isInBackground ? Math.Max(BackgroundIntervalMs, Settings.IntervalMs) : Settings.IntervalMs;

    private void ApplyUnits()
    {
        SensorFormat.UseFahrenheit = Settings.UseFahrenheit;
        foreach (HardwareVM hardware in _hardware)
            hardware.RefreshFormatting();

        RefreshPages();
    }

    private void ResetRanges()
    {
        foreach (HardwareVM hardware in _hardware)
            hardware.ResetRanges();
    }

    // ---- Actions ----

    private void RestartAsAdministrator()
    {
        if (Elevation.RelaunchElevated())
            Application.Current.Shutdown();
    }

    private void RestartNow()
    {
        bool started = Context.Access.IsElevated ? Elevation.Relaunch() : Elevation.RelaunchElevated();
        if (started)
            Application.Current.Shutdown();
    }

    private async Task InstallDriverAsync()
    {
        if (Banner.IsBusy)
            return;

        Banner.IsBusy = true;
        SettingsPage.IsBusy = true;
        try
        {
            int? exitCode = await Elevation.InstallDriverWithWingetAsync();
            AccessInfo after = AccessInfo.Detect();

            if (after.DriverInstalled)
            {
                Banner.Show(
                    $"{AccessInfo.DriverName} installed",
                    "Restart HardwareCenter to start reading CPU and motherboard sensors.",
                    "Restart now", RestartCommand);
                SettingsPage.Status = $"{AccessInfo.DriverName} {after.DriverVersion} installed. Restart HardwareCenter to use it.";
            }
            else if (exitCode == null)
            {
                // No winget on this PC: send the user to the official installer instead.
                Elevation.OpenInBrowser(AccessInfo.DriverHomepage);
                Banner.Show(
                    "Install the driver manually",
                    $"The download page for {AccessInfo.DriverName} has been opened. Run its installer, then restart HardwareCenter.",
                    "Restart now", RestartCommand);
            }
            else
            {
                SettingsPage.Status = $"The {AccessInfo.DriverName} installer did not finish (exit code {exitCode}).";
            }
        }
        finally
        {
            Banner.IsBusy = false;
            SettingsPage.IsBusy = false;
        }
    }

    private void ExportReport()
    {
        if (_latest == null)
            return;

        var dialog = new SaveFileDialog
        {
            Title = "Save hardware report",
            Filter = "Text file (*.txt)|*.txt",
            FileName = $"HardwareCenter report {DateTime.Now:yyyy-MM-dd HHmm}.txt",
            AddExtension = true,
        };

        if (dialog.ShowDialog() != true)
            return;

        string path = dialog.FileName;
        SettingsPage.Status = "Collecting report…";

        // The low-level report must be read on the monitor thread; the rest is assembled back here.
        _engine.Post(() =>
        {
            string detailed = _engine.GetDetailedReport();
            _dispatcher.BeginInvoke(() => WriteReport(path, detailed));
        });
    }

    private void WriteReport(string path, string detailed)
    {
        if (_latest == null)
            return;

        try
        {
            string text = ReportBuilder.Build(_latest, Context.Inventory, Context.Access, LookupRange, detailed);
            File.WriteAllText(path, text);
            SettingsPage.Status = $"Report saved to {path}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SettingsPage.Status = $"Could not save the report: {ex.Message}";
        }
    }

    private (float? Min, float? Max) LookupRange(string sensorKey)
    {
        foreach (HardwareVM hardware in _hardware)
        {
            foreach (SensorVM sensor in hardware.Sensors)
            {
                if (sensor.Key == sensorKey)
                    return (sensor.Min, sensor.Max);
            }
        }

        return (null, null);
    }
}
