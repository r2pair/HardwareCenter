using System.Runtime.InteropServices;

namespace HardwareCenter.Core;

/// <summary>
/// Keeps the app to one running copy per user session. A second launch asks the first to show its
/// window (which may be hidden in the notification area) and then exits.
/// </summary>
public static class SingleInstance
{
    /// <summary>Start another copy anyway (for testing, or comparing two settings side by side).</summary>
    public const string NewInstanceSwitch = "--new-instance";

    public const string MutexName = @"Local\HardwareCenter.SingleInstance";

    /// <summary>Window message, unique to this app, that means "bring your window to the front".</summary>
    public static readonly int ShowMessage = RegisterWindowMessage("HardwareCenter.ShowWindow");

    private static readonly IntPtr Broadcast = new(0xFFFF);
    private const int AllowAnyProcess = -1;
    private const int MessageFilterAllow = 1;

    /// <summary>Asks the copy that is already running to show itself.</summary>
    public static void ShowExisting()
    {
        // Windows only lets a process take the foreground if the current foreground process permits it.
        AllowSetForegroundWindow(AllowAnyProcess);
        PostMessage(Broadcast, ShowMessage, IntPtr.Zero, IntPtr.Zero);
    }

    /// <summary>
    /// Lets an unelevated second launch reach this window when this copy runs as administrator;
    /// Windows otherwise drops messages sent from a lower privilege level.
    /// </summary>
    public static void AcceptShowRequests(IntPtr window) =>
        ChangeWindowMessageFilterEx(window, ShowMessage, MessageFilterAllow, IntPtr.Zero);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "RegisterWindowMessageW")]
    private static extern int RegisterWindowMessage(string name);

    [DllImport("user32.dll", EntryPoint = "PostMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllowSetForegroundWindow(int processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ChangeWindowMessageFilterEx(IntPtr window, int message, int action, IntPtr changeInfo);
}
