using System.ComponentModel;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using HardwareCenter.Core;
using HardwareCenter.ViewModels;

namespace HardwareCenter;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly AppSettings _settings;

    public MainWindow(MainViewModel viewModel, AppSettings settings)
    {
        _viewModel = viewModel;
        _settings = settings;

        InitializeComponent();
        DataContext = viewModel;
        RestorePlacement();

        viewModel.PropertyChanged += OnViewModelPropertyChanged;
        Loaded += (_, _) => ApplyWindowState();

        if (viewModel.IsLoading)
        {
            var spin = new DoubleAnimation(0, 360, TimeSpan.FromSeconds(0.9)) { RepeatBehavior = RepeatBehavior.Forever };
            Timeline.SetDesiredFrameRate(spin, 60);
            SpinnerRotation.BeginAnimation(RotateTransform.AngleProperty, spin);
        }
        StateChanged += (_, _) =>
        {
            ApplyWindowState();
            bool minimized = WindowState == WindowState.Minimized;
            if (!minimized)
                _restoreState = WindowState;

            _viewModel.SetBackground(minimized);

            // A minimized window drawn through the GPU keeps polling the graphics driver to learn when it can
            // present again, which costs CPU for nothing. Software mode has no such polling.
            if (PresentationSource.FromVisual(this) is HwndSource { CompositionTarget: { } target })
                target.RenderMode = minimized ? RenderMode.SoftwareOnly : RenderMode.Default;

            // With the tray icon on, a minimized window leaves the taskbar and lives in the notification area.
            if (minimized && _tray != null)
                Hide();
        };
        SourceInitialized += (_, _) =>
        {
            ApplyDarkFrame();
            IntPtr handle = new WindowInteropHelper(this).Handle;
            HwndSource.FromHwnd(handle)?.AddHook(OnWindowMessage);
            SingleInstance.AcceptShowRequests(handle);
            UpdateTrayIcon();
        };
        viewModel.SettingsPage.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SettingsVM.MinimizeToTray))
                UpdateTrayIcon();
        };
        Closed += (_, _) => _tray?.Dispose();
        Closing += (_, _) => SavePlacement();
    }

    private TrayIcon? _tray;
    private WindowState _restoreState = WindowState.Normal;

    /// <summary>Starts in the notification area without ever showing the window (used at Windows sign-in).</summary>
    public void StartInTray()
    {
        new WindowInteropHelper(this).EnsureHandle();
        if (_tray == null)
        {
            WindowState = WindowState.Minimized;
            Show();
            return;
        }

        _viewModel.SetBackground(true);
    }

    /// <summary>Brings the window back from the notification area, or to the front if it is already open.</summary>
    private void RestoreFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized)
            WindowState = _restoreState;

        _viewModel.SetBackground(false);
        Activate();
    }

    private void UpdateTrayIcon()
    {
        if (_settings.MinimizeToTray && _tray == null)
        {
            _tray = new TrayIcon(new WindowInteropHelper(this).Handle);
            _tray.OpenRequested += RestoreFromTray;
            _tray.ExitRequested += Close;
            _tray.SetTooltip(_viewModel.TrayText);
        }
        else if (!_settings.MinimizeToTray && _tray != null)
        {
            _tray.Dispose();
            _tray = null;

            // Without an icon there would be no way back to a hidden window.
            if (!IsVisible)
                RestoreFromTray();
        }
    }

    private IntPtr OnWindowMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        // Another copy was started: it has exited and asked this one to come forward.
        if (message == SingleInstance.ShowMessage)
        {
            RestoreFromTray();
            handled = true;
        }
        else if (_tray?.HandleMessage(message, lParam) == true)
        {
            handled = true;
        }

        return IntPtr.Zero;
    }

    private void OnMinimize(object sender, RoutedEventArgs e) => SystemCommands.MinimizeWindow(this);

    private void OnMaximizeRestore(object sender, RoutedEventArgs e)
    {
        if (WindowState == WindowState.Maximized)
            SystemCommands.RestoreWindow(this);
        else
            SystemCommands.MaximizeWindow(this);
    }

    private void OnClose(object sender, RoutedEventArgs e) => SystemCommands.CloseWindow(this);

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(MainViewModel.TrayText):
                _tray?.SetTooltip(_viewModel.TrayText);
                break;

            case nameof(MainViewModel.CurrentPage):
                AnimatePageIn();
                break;

            // Once readings arrive the overlay is never needed again; removing it also stops its spinner.
            case nameof(MainViewModel.IsLoading) when !_viewModel.IsLoading:
                SpinnerRotation.BeginAnimation(RotateTransform.AngleProperty, null);
                ContentGrid.Children.Remove(LoadingOverlay);
                break;
        }
    }

    private void AnimatePageIn()
    {
        if (!SystemParameters.ClientAreaAnimation)
            return;

        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var duration = TimeSpan.FromMilliseconds(180);
        PageHost.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, duration) { EasingFunction = ease });
        ((TranslateTransform)PageHost.RenderTransform).BeginAnimation(
            TranslateTransform.YProperty, new DoubleAnimation(10, 0, duration) { EasingFunction = ease });
    }

    /// <summary>
    /// A maximized window with custom chrome hangs over the screen edge by the width of its resize
    /// frame, so the content is inset by that amount while maximized.
    /// </summary>
    private void ApplyWindowState()
    {
        bool maximized = WindowState == WindowState.Maximized;
        if (maximized)
        {
            Thickness frame = SystemParameters.WindowResizeBorderThickness;
            const double paddedBorder = 4;
            Root.Margin = new Thickness(
                frame.Left + paddedBorder, frame.Top + paddedBorder, frame.Right + paddedBorder, frame.Bottom + paddedBorder);
        }
        else
        {
            Root.Margin = new Thickness(0);
        }

        string action = maximized ? "Restore" : "Maximize";
        MaximizeButton.Content = FindResource("Caption." + action);
        MaximizeButton.ToolTip = action;
        System.Windows.Automation.AutomationProperties.SetName(MaximizeButton, action);
    }

    /// <summary>Asks Windows to draw this window's frame and border dark, matching the content.</summary>
    private void ApplyDarkFrame()
    {
        IntPtr handle = new WindowInteropHelper(this).Handle;
        int enabled = 1;
        Native.DwmSetWindowAttribute(handle, Native.DwmwaUseImmersiveDarkMode, ref enabled, sizeof(int));

        // COLORREF is 0x00BBGGRR.
        Color border = (Color)FindResource("C.Raised");
        int colorRef = border.R | (border.G << 8) | (border.B << 16);
        Native.DwmSetWindowAttribute(handle, Native.DwmwaBorderColor, ref colorRef, sizeof(int));

        Color caption = (Color)FindResource("C.Bg");
        int captionRef = caption.R | (caption.G << 8) | (caption.B << 16);
        Native.DwmSetWindowAttribute(handle, Native.DwmwaCaptionColor, ref captionRef, sizeof(int));
    }

    private void RestorePlacement()
    {
        Rect workArea = SystemParameters.WorkArea;

        if (_settings is { WindowLeft: double left, WindowTop: double top, WindowWidth: double width, WindowHeight: double height })
        {
            var saved = new Rect(left, top, Math.Max(width, MinWidth), Math.Max(height, MinHeight));
            var desktop = new Rect(
                SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
                SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);

            // Only reuse the saved position if most of the title bar would still be reachable.
            var titleBar = new Rect(saved.Left + 60, saved.Top, Math.Max(1, saved.Width - 120), 40);
            if (desktop.Contains(titleBar))
            {
                WindowStartupLocation = WindowStartupLocation.Manual;
                Left = saved.Left;
                Top = saved.Top;
                Width = saved.Width;
                Height = saved.Height;
            }
        }
        else
        {
            Width = Math.Max(MinWidth, Math.Min(Width, workArea.Width - 24));
            Height = Math.Max(MinHeight, Math.Min(Height, workArea.Height - 24));
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }

        if (_settings.WindowMaximized)
            WindowState = WindowState.Maximized;
    }

    private void SavePlacement()
    {
        // RestoreBounds is the normal-state rectangle even while maximized or minimized.
        Rect bounds = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
        if (!bounds.IsEmpty && bounds.Width > 0 && bounds.Height > 0)
        {
            _settings.WindowLeft = bounds.Left;
            _settings.WindowTop = bounds.Top;
            _settings.WindowWidth = bounds.Width;
            _settings.WindowHeight = bounds.Height;
        }

        // Closed from the notification area, the window is minimized; remember how it was before that.
        _settings.WindowMaximized = (WindowState == WindowState.Minimized ? _restoreState : WindowState) == WindowState.Maximized;
        _settings.Save();
    }
}
