using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace HardwareCenter.Core;

/// <summary>
/// The notification-area icon, done with the Win32 call directly so the app does not have to load
/// Windows Forms just for this. Messages arrive through the owning window's message hook.
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private const int CallbackMessage = 0x8000 + 1; // WM_APP + 1
    private const int WmLeftButtonUp = 0x0202;
    private const int WmLeftButtonDoubleClick = 0x0203;
    private const int WmRightButtonUp = 0x0205;
    private const int NimAdd = 0, NimModify = 1, NimDelete = 2;
    private const int NifMessage = 1, NifIcon = 2, NifTip = 4;
    private const int MenuOpen = 1, MenuExit = 2;
    private const int MaxTooltip = 127;

    private static readonly int TaskbarCreated = RegisterWindowMessage("TaskbarCreated");

    private readonly IntPtr _window;
    private readonly IntPtr _icon;
    private readonly bool _ownsIcon;
    private string _tooltip = "HardwareCenter";
    private bool _added;

    public TrayIcon(IntPtr window)
    {
        _window = window;

        // The application icon embedded in the exe (resource 32512), at the small-icon size.
        _icon = LoadImage(GetModuleHandle(null), new IntPtr(32512), 1, GetSystemMetrics(49), GetSystemMetrics(50), 0);
        _ownsIcon = _icon != IntPtr.Zero;
        if (_icon == IntPtr.Zero)
            _icon = LoadIcon(IntPtr.Zero, new IntPtr(32512)); // shared system icon: must not be destroyed

        Add();
    }

    /// <summary>The icon was clicked, or "Open" was chosen from its menu.</summary>
    public event Action? OpenRequested;

    /// <summary>"Exit" was chosen from the icon's menu.</summary>
    public event Action? ExitRequested;

    public void SetTooltip(string text)
    {
        if (text.Length > MaxTooltip)
            text = text[..MaxTooltip];

        if (text == _tooltip)
            return;

        _tooltip = text;
        if (_added)
        {
            NotifyIconData data = CreateData(NifTip);
            Shell_NotifyIcon(NimModify, ref data);
        }
    }

    /// <summary>Call from the owning window's message hook. Returns true if the message was for the icon.</summary>
    public bool HandleMessage(int message, IntPtr lParam)
    {
        // Explorer restarted: the notification area is new and has forgotten every icon.
        if (message == TaskbarCreated)
        {
            _added = false;
            Add();
            return false;
        }

        if (message != CallbackMessage)
            return false;

        switch (lParam.ToInt32() & 0xFFFF)
        {
            case WmLeftButtonUp:
            case WmLeftButtonDoubleClick:
                OpenRequested?.Invoke();
                break;
            case WmRightButtonUp:
                ShowMenu();
                break;
        }

        return true;
    }

    public void Dispose()
    {
        if (_added)
        {
            NotifyIconData data = CreateData(0);
            Shell_NotifyIcon(NimDelete, ref data);
            _added = false;
        }

        if (_ownsIcon && _icon != IntPtr.Zero)
            DestroyIcon(_icon);
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr icon);

    private void Add()
    {
        NotifyIconData data = CreateData(NifMessage | NifIcon | NifTip);
        _added = Shell_NotifyIcon(NimAdd, ref data);
    }

    private void ShowMenu()
    {
        IntPtr menu = CreatePopupMenu();
        if (menu == IntPtr.Zero)
            return;

        try
        {
            AppendMenu(menu, 0, new IntPtr(MenuOpen), "Open HardwareCenter");
            AppendMenu(menu, 0x800, IntPtr.Zero, null); // separator
            AppendMenu(menu, 0, new IntPtr(MenuExit), "Exit");

            // The menu only closes on an outside click if its owner is the foreground window.
            GetCursorPos(out Point cursor);
            SetForegroundWindow(_window);
            int command = TrackPopupMenuEx(menu, 0x0100 | 0x0002, cursor.X, cursor.Y, _window, IntPtr.Zero); // RETURNCMD | RIGHTBUTTON
            PostMessage(_window, 0, IntPtr.Zero, IntPtr.Zero);

            if (command == MenuOpen)
                OpenRequested?.Invoke();
            else if (command == MenuExit)
                ExitRequested?.Invoke();
        }
        finally
        {
            DestroyMenu(menu);
        }
    }

    private NotifyIconData CreateData(int flags) => new()
    {
        Size = Marshal.SizeOf<NotifyIconData>(),
        Window = _window,
        Id = 1,
        Flags = flags,
        CallbackMessage = CallbackMessage,
        Icon = _icon,
        Tip = _tooltip,
        Info = "",
        InfoTitle = "",
    };

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public int Size;
        public IntPtr Window;
        public int Id;
        public int Flags;
        public int CallbackMessage;
        public IntPtr Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Tip;
        public int State;
        public int StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Info;
        public int Version;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string InfoTitle;
        public int InfoFlags;
        public Guid GuidItem;
        public IntPtr BalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X, Y;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "Shell_NotifyIconW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Shell_NotifyIcon(int message, ref NotifyIconData data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "RegisterWindowMessageW")]
    private static extern int RegisterWindowMessage(string name);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "LoadImageW")]
    private static extern IntPtr LoadImage(IntPtr instance, IntPtr name, int type, int width, int height, int flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "LoadIconW")]
    private static extern IntPtr LoadIcon(IntPtr instance, IntPtr name);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetModuleHandleW")]
    private static extern IntPtr GetModuleHandle(string? name);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll")]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "AppendMenuW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AppendMenu(IntPtr menu, int flags, IntPtr id, string? text);

    [DllImport("user32.dll")]
    private static extern int TrackPopupMenuEx(IntPtr menu, int flags, int x, int y, IntPtr window, IntPtr parameters);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyMenu(IntPtr menu);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out Point point);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr window);

    [DllImport("user32.dll", EntryPoint = "PostMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);
}

/// <summary>The per-user "run at sign-in" entry.</summary>
public static class StartupEntry
{
    public const string MinimizedSwitch = "--minimized";

    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "HardwareCenter";

    public static bool IsEnabled
    {
        get
        {
            try
            {
                using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKey);
                return key?.GetValue(ValueName) is string command
                       && Environment.ProcessPath is string exe
                       && command.Contains(exe, StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException)
            {
                return false;
            }
        }
    }

    /// <summary>Adds or removes the entry. Returns false if the registry could not be changed.</summary>
    public static bool Set(bool enabled)
    {
        try
        {
            using RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (enabled && Environment.ProcessPath is string exe)
                key.SetValue(ValueName, $"\"{exe}\" {MinimizedSwitch}");
            else
                key.DeleteValue(ValueName, throwOnMissingValue: false);

            return true;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
