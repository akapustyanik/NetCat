using System;
using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using Microsoft.Win32;

namespace NetCat.UI.SystemIntegration
{
    public class TaskSchedulerManager
    {
        private const string TaskName = "NetCat_AutoStart";

        public static bool IsRunAsAdmin()
        {
            var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }

        public static bool IsTaskScheduled()
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "schtasks.exe",
                    Arguments = $"/query /tn \"{TaskName}\"",
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };

                using var proc = Process.Start(psi);
                proc?.WaitForExit(2000);
                return proc?.ExitCode == 0;
            }
            catch
            {
                return false;
            }
        }

        public static bool RegisterAutoStartTask()
        {
            try
            {
                string exePath = Process.GetCurrentProcess().MainModule?.FileName ??
                                 Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "NetCat.exe");

                // Create task with /RL HIGHEST (highest privileges) at logon of user (/SC ONLOGON)
                string args = $"/create /tn \"{TaskName}\" /tr \"\\\"{exePath}\\\" --minimized\" /sc ONLOGON /rl HIGHEST /f";

                var psi = new ProcessStartInfo
                {
                    FileName = "schtasks.exe",
                    Arguments = args,
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };

                using var proc = Process.Start(psi);
                proc?.WaitForExit(3000);
                return proc?.ExitCode == 0;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to register autostart task: {ex.Message}");
                return false;
            }
        }

        public static bool UnregisterAutoStartTask()
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "schtasks.exe",
                    Arguments = $"/delete /tn \"{TaskName}\" /f",
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };

                using var proc = Process.Start(psi);
                proc?.WaitForExit(3000);
                return proc?.ExitCode == 0;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to delete autostart task: {ex.Message}");
                return false;
            }
        }
    }

    public class SystemProxyManager
    {
        private const string InternetSettingsKey = @"Software\Microsoft\Windows\CurrentVersion\Internet Settings";

        public static void SetSystemProxy(bool enable, string proxyServer = "127.0.0.1:10808")
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(InternetSettingsKey, true);
                if (key != null)
                {
                    key.SetValue("ProxyEnable", enable ? 1 : 0);
                    if (enable)
                    {
                        key.SetValue("ProxyServer", proxyServer);
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to set system proxy: {ex.Message}");
            }
        }

        public static bool IsSystemProxyEnabled()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(InternetSettingsKey, false);
                if (key != null)
                {
                    var val = key.GetValue("ProxyEnable");
                    return val != null && Convert.ToInt32(val) == 1;
                }
            }
            catch { }
            return false;
        }
    }
}
