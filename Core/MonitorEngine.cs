using System.Diagnostics;
using System.Runtime;

namespace HardwareCenter.Core;

/// <summary>
/// Owns the sensor sources and polls them on a dedicated background thread. Events are raised on that
/// thread; subscribers marshal to the UI thread themselves.
/// </summary>
public sealed class MonitorEngine : IDisposable
{
    private readonly ManualResetEventSlim _wake = new(false);
    private readonly object _pendingLock = new();
    private readonly List<Action> _pending = [];

    private LhmSource? _lhm;
    private DiskProbe? _disks;
    private NicCatalog? _nics;
    private CpuSpeedProbe? _cpuSpeed;
    private Thread? _thread;
    private volatile bool _stopRequested;
    private volatile int _intervalMs = 1000;

    public event Action<Snapshot>? SnapshotReady;
    public event Action<Inventory>? InventoryReady;
    public event Action<AccessInfo>? AccessDetected;

    public AccessInfo Access { get; private set; } = new(false, false, null, false);

    /// <summary>Set if the sensor library failed to initialize; Windows-level data is still collected.</summary>
    public string? InitializationError { get; private set; }

    public int IntervalMs
    {
        get => _intervalMs;
        set
        {
            _intervalMs = Math.Clamp(value, 250, 10000);
            _wake.Set();
        }
    }

    public void Start()
    {
        if (_thread != null)
            return;

        _thread = new Thread(Run) { IsBackground = true, Name = "HardwareMonitor" };
        _thread.Start();
    }

    /// <summary>Queues work to run on the monitor thread between updates, where the sensor sources live.</summary>
    public void Post(Action action)
    {
        lock (_pendingLock)
            _pending.Add(action);

        _wake.Set();
    }

    /// <summary>Text of the sensor library's own diagnostic report. Must run on the monitor thread (see <see cref="Post"/>).</summary>
    public string GetDetailedReport() => _lhm?.GetDetailedReport() ?? "";

    /// <summary>Opens the sensor sources on the calling thread. Used directly by the headless report mode.</summary>
    public void Initialize()
    {
        Access = AccessInfo.Detect();
        AccessDetected?.Invoke(Access);

        var lhm = new LhmSource();
        try
        {
            lhm.Open();
            _lhm = lhm;
        }
        catch (Exception ex)
        {
            InitializationError = ex.Message;
            lhm.Dispose();
        }

        _disks = new DiskProbe();
        _nics = new NicCatalog();
        if (!Access.DriverReady)
            _cpuSpeed = new CpuSpeedProbe();
    }

    public Inventory CollectInventory() => InventoryCollector.Collect(_lhm?.ReadSmbios() ?? SmbiosData.Empty);

    /// <summary>Runs one update pass on the calling thread and returns what was read.</summary>
    public Snapshot Tick()
    {
        var stopwatch = Stopwatch.StartNew();
        DateTime now = DateTime.UtcNow;
        var hardware = new List<HardwareReading>(48);

        _lhm?.Update();
        _lhm?.Collect(hardware, Access.DriverReady);

        // Unelevated, the sensor library sees no drives at all; fill in from Windows.
        bool needDrives = !hardware.Exists(h => h.Kind == HardwareKind.Storage);
        _disks?.Update(now, sample: needDrives);
        _nics?.Update(now);

        if (needDrives)
            _disks?.Collect(hardware);

        if (_cpuSpeed != null)
            AddCpuSpeed(hardware, _cpuSpeed.Sample());

        return new Snapshot(now, hardware, _disks?.Disks ?? [], _nics?.Nics ?? [], stopwatch.Elapsed.TotalMilliseconds);
    }

    private static void AddCpuSpeed(List<HardwareReading> hardware, float? mhz)
    {
        if (mhz == null)
            return;

        int index = hardware.FindIndex(h => h.Kind == HardwareKind.Cpu);
        if (index < 0 || hardware[index].Sensors.Any(s => s.Kind == SensorKind.Clock))
            return;

        HardwareReading cpu = hardware[index];
        var sensors = new List<SensorReading>(cpu.Sensors)
        {
            new($"{cpu.Key}/clock/windows|Average Effective Speed", "Average Effective Speed", SensorKind.Clock, 0, mhz),
        };
        hardware[index] = cpu with { Sensors = sensors };
    }

    private void Run()
    {
        try
        {
            Initialize();
        }
        catch (Exception ex)
        {
            InitializationError ??= ex.Message;
        }

        // WMI queries take a noticeable fraction of a second; keep them off the sampling loop.
        SmbiosData smbios = _lhm?.ReadSmbios() ?? SmbiosData.Empty;
        Task.Run(() =>
        {
            try
            {
                InventoryReady?.Invoke(InventoryCollector.Collect(smbios));
            }
            catch
            {
                InventoryReady?.Invoke(new Inventory { Smbios = smbios, ComputerName = Environment.MachineName });
            }

            // Discovering hardware allocates a great deal exactly once. A monitor is left running for
            // hours, so hand that memory back to Windows now instead of carrying it.
            GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        });

        var clock = Stopwatch.StartNew();
        while (!_stopRequested)
        {
            long started = clock.ElapsedMilliseconds;

            try
            {
                SnapshotReady?.Invoke(Tick());
            }
            catch
            {
                // A failed pass is skipped; the next one starts from scratch.
            }

            RunPending();

            int remaining = _intervalMs - (int)(clock.ElapsedMilliseconds - started);
            _wake.Wait(Math.Max(50, remaining));
            _wake.Reset();
        }

        RunPending();
        _cpuSpeed?.Dispose();
        _lhm?.Dispose();
    }

    private void RunPending()
    {
        Action[] actions;
        lock (_pendingLock)
        {
            if (_pending.Count == 0)
                return;

            actions = _pending.ToArray();
            _pending.Clear();
        }

        foreach (Action action in actions)
        {
            try
            {
                action();
            }
            catch
            {
                // Posted work reports its own failures.
            }
        }
    }

    public void Dispose()
    {
        _stopRequested = true;
        _wake.Set();

        if (_thread != null)
        {
            // Closing the sensor library unloads driver modules; give it a moment but never hang the exit.
            _thread.Join(TimeSpan.FromSeconds(3));
            _thread = null;
        }
        else
        {
            _cpuSpeed?.Dispose();
            _lhm?.Dispose();
        }
    }
}
