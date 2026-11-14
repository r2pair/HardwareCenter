using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using HardwareCenter.Core;
using HardwareCenter.ViewModels;

namespace HardwareCenter;

public partial class App : Application
{
    /// <summary>Faults survived within <see cref="ErrorWindow"/> before the app gives up.</summary>
    private const int MaxRecoverableErrors = 5;
    private static readonly TimeSpan ErrorWindow = TimeSpan.FromMinutes(1);
    private const long MaxErrorLogBytes = 1024 * 1024;

    private MainViewModel? _viewModel;
    private Mutex? _instanceMutex;
    private int _errorCount;
    private DateTime _firstErrorUtc;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // HardwareCenter.exe --report <file> [--detailed]: write a report and exit without showing a window.
        int reportIndex = IndexOf(e.Args, "--report");
        if (reportIndex >= 0)
        {
            string path = reportIndex + 1 < e.Args.Length ? e.Args[reportIndex + 1] : "HardwareCenter report.txt";
            Shutdown(WriteReport(path, detailed: Has(e.Args, "--detailed")));
            return;
        }

        DispatcherUnhandledException += OnUnhandledException;

        // One window per user: a second launch brings the first to the front and exits. Without this,
        // starting the app again while it sits in the notification area would leave two copies running.
        if (!Has(e.Args, SingleInstance.NewInstanceSwitch) && !AcquireInstance(waitForPrevious: Has(e.Args, Elevation.RestartedSwitch)))
        {
            SingleInstance.ShowExisting();
            Shutdown();
            return;
        }

        // Experimental: draw without the GPU (slightly less memory; CPU cost unclear).
        if (Has(e.Args, "--software-render"))
            System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;

        AppSettings settings = AppSettings.Load();
        if (settings.AlwaysRunAsAdministrator && !Has(e.Args, Elevation.NoElevateSwitch)
            && !AccessInfo.Detect().IsElevated && Elevation.RelaunchElevated(e.Args))
        {
            Shutdown();
            return;
        }

        _viewModel = new MainViewModel(settings);

        // HardwareCenter.exe --page Thermals: open on a particular page (handy for shortcuts).
        int pageIndex = IndexOf(e.Args, "--page");
        if (pageIndex >= 0 && pageIndex + 1 < e.Args.Length)
            _viewModel.SelectPage(e.Args[pageIndex + 1]);

        var window = new MainWindow(_viewModel, settings);
        MainWindow = window;

        // --minimized (used by the start-with-Windows entry): go straight to the notification area.
        if (Has(e.Args, StartupEntry.MinimizedSwitch))
            window.StartInTray();
        else
            window.Show();

        _viewModel.Start();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _viewModel?.Dispose();
        base.OnExit(e);
    }

    private static bool Has(string[] args, string name) => IndexOf(args, name) >= 0;

    private static int IndexOf(string[] args, string name) =>
        Array.FindIndex(args, a => a.Equals(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Claims the single-instance mutex for the life of the process. After a restart (to elevate, or
    /// to pick up a new driver) the previous copy is still shutting down, so wait for it briefly.
    /// </summary>
    private bool AcquireInstance(bool waitForPrevious)
    {
        try
        {
            _instanceMutex = new Mutex(false, SingleInstance.MutexName);
            return _instanceMutex.WaitOne(waitForPrevious ? TimeSpan.FromSeconds(8) : TimeSpan.Zero);
        }
        catch (AbandonedMutexException)
        {
            return true; // the previous owner exited without releasing it; it is ours now
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or WaitHandleCannotBeOpenedException or IOException)
        {
            // An elevated copy owns it and this unelevated one may not even open it.
            return false;
        }
    }

    private static int WriteReport(string path, bool detailed)
    {
        try
        {
            using var engine = new MonitorEngine();
            engine.Initialize();

            // Rates and loads are differences between passes, so take a few before reporting.
            Snapshot snapshot = engine.Tick();
            for (int pass = 0; pass < 3; pass++)
            {
                Thread.Sleep(700);
                snapshot = engine.Tick();
            }

            string report = ReportBuilder.Build(
                snapshot, engine.CollectInventory(), engine.Access,
                detailed: detailed ? engine.GetDetailedReport() : null);

            File.WriteAllText(Path.GetFullPath(path), report, ReportBuilder.FileEncoding);
            return 0;
        }
        catch (Exception ex)
        {
            LogError(ex);
            return 1;
        }
    }

    /// <summary>
    /// A fault while drawing or updating one view should not take the whole monitor down, so isolated
    /// faults are logged and survived. Several in quick succession mean something is truly broken:
    /// say so and stop. The count starts over once a minute passes, so an app left running for days
    /// is not closed by a handful of unrelated hiccups.
    /// </summary>
    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        LogError(e.Exception);
        e.Handled = true;

        DateTime now = DateTime.UtcNow;
        if (now - _firstErrorUtc > ErrorWindow)
        {
            _firstErrorUtc = now;
            _errorCount = 0;
        }

        if (++_errorCount <= MaxRecoverableErrors)
            return;

        MessageBox.Show(
            $"HardwareCenter ran into a problem it could not recover from and needs to close.\n\n{e.Exception.Message}\n\nDetails were saved to:\n{ErrorLogPath}",
            "HardwareCenter", MessageBoxButton.OK, MessageBoxImage.Error);
        Shutdown(1);
    }

    private static string ErrorLogPath => Path.Combine(AppSettings.DataDirectory, "error.log");

    private static void LogError(Exception exception)
    {
        try
        {
            Directory.CreateDirectory(AppSettings.DataDirectory);

            // Start the log again rather than let a repeating fault fill the disk.
            var file = new FileInfo(ErrorLogPath);
            if (file.Exists && file.Length > MaxErrorLogBytes)
                file.Delete();

            File.AppendAllText(
                ErrorLogPath,
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {exception}{Environment.NewLine}{Environment.NewLine}",
                Encoding.UTF8);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Nowhere to write the log; the original error is still surfaced to the user if it repeats.
        }
    }
}
