using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace HardwareCenter.Core;

public enum AccessLevel
{
    /// <summary>Not elevated: OS-level metrics, GPU, memory, network and basic drive data only.</summary>
    Limited,

    /// <summary>Elevated, but the PawnIO low-level sensor driver is not installed.</summary>
    DriverMissing,

    /// <summary>Elevated and the driver is installed, but its device cannot be opened.</summary>
    DriverUnavailable,

    /// <summary>Elevated with a working driver: every sensor is readable.</summary>
    Full,
}

/// <summary>
/// Which sensors this process is allowed to read. CPU temperatures, clocks and power, plus motherboard
/// fans and voltages, are read through the PawnIO kernel driver; drive health (SMART) needs elevation.
/// </summary>
public sealed record AccessInfo(bool IsElevated, bool DriverInstalled, string? DriverVersion, bool DriverReady)
{
    public const string DriverName = "PawnIO";
    public const string DriverHomepage = "https://pawnio.eu/";
    public const string DriverWingetId = "namazso.PawnIO";

    public AccessLevel Level =>
        !IsElevated ? AccessLevel.Limited
        : DriverReady ? AccessLevel.Full
        : DriverInstalled ? AccessLevel.DriverUnavailable
        : AccessLevel.DriverMissing;

    public static AccessInfo Detect()
    {
        bool elevated = IsProcessElevated();
        string? version = ReadDriverVersion();
        return new AccessInfo(elevated, version != null, version, CanOpenDriver());
    }

    private static bool IsProcessElevated()
    {
        try
        {
            using WindowsIdentity identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>The driver's installer registers itself under the standard uninstall key.</summary>
    private static string? ReadDriverVersion()
    {
        const string subKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\PawnIO";

        foreach (RegistryView view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            try
            {
                using RegistryKey root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                using RegistryKey? key = root.OpenSubKey(subKey);
                if (key?.GetValue("DisplayVersion") is string version && version.Length > 0)
                    return version;
            }
            catch
            {
                // Unreadable registry view: treat as not installed.
            }
        }

        return null;
    }

    private static bool CanOpenDriver()
    {
        try
        {
            using SafeFileHandle handle = Native.CreateFile(
                @"\\?\GLOBALROOT\Device\PawnIO",
                Native.GenericRead | Native.GenericWrite,
                Native.ShareReadWrite, IntPtr.Zero, Native.OpenExisting, 0, IntPtr.Zero);

            return !handle.IsInvalid;
        }
        catch
        {
            return false;
        }
    }
}

/// <summary>Restarting with administrator rights and installing the sensor driver.</summary>
public static class Elevation
{
    public const string NoElevateSwitch = "--no-elevate";

    /// <summary>Starts a new elevated copy of this app. Returns false if the user declined the UAC prompt.</summary>
    public static bool RelaunchElevated()
    {
        string? exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe))
            return false;

        try
        {
            Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, Verb = "runas" });
            return true;
        }
        catch (Win32Exception)
        {
            return false; // UAC prompt cancelled
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>Starts a new copy of this app at the current privilege level.</summary>
    public static bool Relaunch()
    {
        string? exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe))
            return false;

        try
        {
            Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, Arguments = NoElevateSwitch });
            return true;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>
    /// Installs the PawnIO driver through winget in a visible console window, so the user sees exactly
    /// what runs. Returns the installer's exit code, or null if winget is not available.
    /// </summary>
    public static async Task<int?> InstallDriverWithWingetAsync()
    {
        var startInfo = new ProcessStartInfo("winget")
        {
            Arguments = $"install --id {AccessInfo.DriverWingetId} --exact --accept-source-agreements --accept-package-agreements",
            UseShellExecute = true,
        };

        try
        {
            using Process? process = Process.Start(startInfo);
            if (process == null)
                return null;

            await process.WaitForExitAsync().ConfigureAwait(false);
            return process.ExitCode;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            return null; // winget missing
        }
    }

    public static void OpenInBrowser(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            // No default browser: nothing useful to do.
        }
    }
}
