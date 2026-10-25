// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace RECompose.Presentation.Services;

/// <summary>
/// Power and sleep (REL-25, PLAT-60): whether the machine runs on battery, and keeping it awake while the service runs. Windows
/// uses the system calls; macOS uses <c>pmset</c> and <c>caffeinate</c>; elsewhere nothing is known and nothing is done.
/// </summary>
public static class SystemPower
{
    private static Process? _caffeinate;

    /// <summary>On battery power; null when unknown or there is no battery.</summary>
    public static bool? OnBattery => Status() is { } s ? s.OnBattery : null;

    public static int? BatteryPercent => Status() is { } s ? s.Percent : null;

    public readonly record struct PowerStatus(bool OnBattery, int? Percent);

    /// <summary>Tests and tools can replace the reading.</summary>
    public static Func<PowerStatus?>? Override { get; set; }

    private static PowerStatus? Status()
    {
        if (Override is { } custom)
            return custom();

        try
        {
            if (OperatingSystem.IsWindows())
                return WindowsStatus();
            if (OperatingSystem.IsMacOS())
                return MacStatus();
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
        {
        }

        return null;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemPowerStatus
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public int BatteryLifeTime;
        public int BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemPowerStatus(out SystemPowerStatus status);

    [DllImport("kernel32.dll")]
    private static extern uint SetThreadExecutionState(uint flags);

    private const uint EsContinuous = 0x80000000;
    private const uint EsSystemRequired = 0x00000001;
    private const uint EsDisplayRequired = 0x00000002;

    private static PowerStatus? WindowsStatus()
    {
        if (!GetSystemPowerStatus(out var s) || s.BatteryFlag == 128 /* no battery */ || s.BatteryFlag == 255)
            return null;
        return new PowerStatus(s.ACLineStatus == 0, s.BatteryLifePercent <= 100 ? s.BatteryLifePercent : null);
    }

    private static PowerStatus? MacStatus()
    {
        using var process = Process.Start(new ProcessStartInfo("pmset", "-g batt") { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true });
        if (process is null)
            return null;
        var text = process.StandardOutput.ReadToEnd();
        process.WaitForExit(2000);
        if (!text.Contains("InternalBattery", StringComparison.Ordinal))
            return null;
        var percent = System.Text.RegularExpressions.Regex.Match(text, @"(\d+)%");
        return new PowerStatus(text.Contains("Battery Power", StringComparison.Ordinal), percent.Success ? int.Parse(percent.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) : null);
    }

    /// <summary>
    /// Keeps the system and the display awake until <see cref="AllowSleep"/> (PLAT-60). Calling it again is harmless.
    /// </summary>
    public static bool PreventSleep()
    {
        if (OperatingSystem.IsWindows())
        {
            try
            {
                return SetThreadExecutionState(EsContinuous | EsSystemRequired | EsDisplayRequired) != 0;
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
            {
                return false;
            }
        }

        if (OperatingSystem.IsMacOS() && _caffeinate is null)
        {
            try
            {
                _caffeinate = Process.Start(new ProcessStartInfo("caffeinate", "-d -i") { UseShellExecute = false, CreateNoWindow = true });
                return _caffeinate is not null;
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                return false;
            }
        }

        return _caffeinate is not null;
    }

    public static void AllowSleep()
    {
        if (OperatingSystem.IsWindows())
        {
            try
            {
                SetThreadExecutionState(EsContinuous);
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
            {
            }
        }

        if (_caffeinate is { } process)
        {
            try
            {
                if (!process.HasExited)
                    process.Kill();
            }
            catch (InvalidOperationException)
            {
            }

            process.Dispose();
            _caffeinate = null;
        }
    }
}
