using System.Diagnostics;
using Microsoft.Win32;
using ZoneTitan.Core.Diagnostics;

namespace ZoneTitan.App.Services;

/// <summary>
/// Start-with-Windows via the HKCU Run key.
/// <para>
/// Registry rather than Task Scheduler, because the Run key needs no admin rights and
/// ZoneTitan runs as a standard user (spec §1). Task Scheduler only becomes worthwhile if the
/// user opts into running elevated to beat the UIPI hotkey caveat in spec §6.
/// </para>
/// </summary>
internal static class AutoStart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "ZoneTitan";

    private static string ExecutablePath =>
        Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName ?? "";

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string existing && existing.Contains("ZoneTitan");
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not read auto-start state: {ex.Message}");
            return false;
        }
    }

    public static bool SetEnabled(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (key is null)
                return false;

            if (enabled)
            {
                var path = ExecutablePath;
                if (string.IsNullOrWhiteSpace(path))
                    return false;

                key.SetValue(ValueName, $"\"{path}\" --tray");
                Log.Info($"Auto-start enabled: {path}");
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
                Log.Info("Auto-start disabled.");
            }

            return true;
        }
        catch (Exception ex)
        {
            Log.Error($"Could not change auto-start: {ex.Message}");
            return false;
        }
    }
}
