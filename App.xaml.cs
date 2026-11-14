using System.IO;
using System.Windows;
using System.Windows.Threading;
using HardwareCenter.Core;
using HardwareCenter.ViewModels;

namespace HardwareCenter;

public partial class App : Application
{
    private const int MaxRecoverableErrors = 5;

    private MainViewModel? _viewModel;
    private int _errorCount;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // HardwareCenter.exe --report <file> [--detailed]: write a report and exit without showing a window.
        int reportIndex = Array.FindIndex(e.Args, a => a.Equals("--report", StringComparison.OrdinalIgnoreCase));
        if (reportIndex >= 0)
        {
            string path = reportIndex + 1 < e.Args.Length ? e.Args[reportIndex + 1] : "HardwareCenter report.txt";
            bool detailed = e.Args.Any(a => a.Equals("--detailed", StringComparison.OrdinalIgnoreCase));
            Shutdown(WriteReport(path, detailed));
            return;
        }

        DispatcherUnhandledException += OnUnhandledException;

        // Experimental: draw without the GPU (slightly less memory; CPU cost unclear).
        if (e.Args.Any(a => a.Equals("--software-render", StringComparison.OrdinalIgnoreCase)))
            System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;


        AppSettings settings = AppSettings.Load();
        bool mayElevate = !e.Args.Any(a => a.Equals(Elevation.NoElevateSwitch, StringComparison.OrdinalIgnoreCase));
        if (settings.AlwaysRunAsAdministrator && mayElevate && !AccessInfo.Detect().IsElevated && Elevation.RelaunchElevated())
        {
            Shutdown();
            return;
        }

        _viewModel = new MainViewModel(settings);

        // HardwareCenter.exe --page Thermals: open on a particular page (handy for shortcuts).
        int pageIndex = Array.FindIndex(e.Args, a => a.Equals("--page", StringComparison.OrdinalIgnoreCase));
        if (pageIndex >= 0 && pageIndex + 1 < e.Args.Length)
            _viewModel.SelectPage(e.Args[pageIndex + 1]);

        var window = new MainWindow(_viewModel, settings);
        MainWindow = window;

        // --minimized (used by the start-with-Windows entry): go straight to the notification area.
        if (e.Args.Any(a => a.Equals(StartupEntry.MinimizedSwitch, StringComparison.OrdinalIgnoreCase)))
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

            File.WriteAllText(Path.GetFullPath(path), report);
            return 0;
        }
        catch (Exception ex)
        {
            LogError(ex);
            return 1;
        }
    }

    /// <summary>
    /// A fault while drawing or updating one view should not take the whole monitor down, so the first
    /// few are logged and survived. Repeated faults mean something is truly broken: say so and stop.
    /// </summary>
    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        LogError(e.Exception);

        if (++_errorCount <= MaxRecoverableErrors)
        {
            e.Handled = true;
            return;
        }

        e.Handled = true;
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
            File.AppendAllText(ErrorLogPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {exception}{Environment.NewLine}{Environment.NewLine}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Nowhere to write the log; the original error is still surfaced to the user if it repeats.
        }
    }
}
