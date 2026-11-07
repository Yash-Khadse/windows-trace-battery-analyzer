using System.Runtime.InteropServices;
using System.Security.Principal;

namespace Wtba.Collector;

/// <summary>Read-only environment probes.</summary>
public static class SystemStatus
{
    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEM_POWER_STATUS
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag; // bit 0 (value 1) = battery saver on
        public uint BatteryLifeTime;
        public uint BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS s);

    public static bool IsElevated()
    {
        if (!OperatingSystem.IsWindows()) return false;
        using var id = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
    }

    public static double FreeDiskGb(string path)
    {
        var root = Path.GetPathRoot(Path.GetFullPath(path))!;
        return new DriveInfo(root).AvailableFreeSpace / 1024.0 / 1024.0 / 1024.0;
    }

    /// <summary>Raw SYSTEM_POWER_STATUS.SystemStatusFlag (bit 0 = battery saver), or -1 if unavailable.</summary>
    public static int ReadRawFlag() =>
        OperatingSystem.IsWindows() && GetSystemPowerStatus(out var s) ? s.SystemStatusFlag : -1;

    /// <summary>
    /// Read-only: HKLM\SYSTEM\CurrentControlSet\Control\Power\EnergySaverState. Observed on Windows 11 25H2 by toggling
    /// Energy saver by hand: 1 = on, 2 = off, while GetSystemPowerStatus and WinRT EnergySaverStatus did NOT change.
    /// Returns null when the value is absent or unreadable. Never writes.
    /// </summary>
    public static int? ReadEnergySaverState()
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            return Microsoft.Win32.Registry.GetValue(
                @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Power", "EnergySaverState", null) as int?;
        }
        catch (Exception) { return null; }
    }

    /// <summary>Energy saver / Battery saver is on if either the documented flag bit or the observed registry state says so.</summary>
    public static bool ResolveSaver(int systemStatusFlag, int? energySaverState) =>
        (systemStatusFlag > 0 && (systemStatusFlag & 1) != 0) || energySaverState == 1;

    /// <summary>
    /// Pure interpretation of SYSTEM_POWER_STATUS, failing closed: unless AC is explicitly 0 (battery) or 1 (AC),
    /// a battery is present and the percentage is 0..100, the percentage is reported as 255 (unknown).
    /// </summary>
    public static (int Pct, bool OnAc, bool BatterySaver) Interpret(byte acLine, byte batteryFlag, byte pct, int statusFlag, int? energySaverState)
    {
        bool known = (acLine == 0 || acLine == 1) && pct <= 100 && (batteryFlag & 128) == 0 && batteryFlag != 255;
        return (known ? pct : 255, acLine == 1, ResolveSaver(statusFlag, energySaverState));
    }

    /// <summary>Returns (batteryPct, 255 = UNKNOWN (failed read, no battery, unknown AC state); onAc; batterySaver).</summary>
    public static (int Pct, bool OnAc, bool BatterySaver) ReadPower()
    {
        if (!OperatingSystem.IsWindows() || !GetSystemPowerStatus(out var s)) return (255, false, false);
        return Interpret(s.ACLineStatus, s.BatteryFlag, s.BatteryLifePercent, s.SystemStatusFlag, ReadEnergySaverState());
    }
}
