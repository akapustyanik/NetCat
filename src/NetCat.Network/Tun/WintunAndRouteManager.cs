using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text;

namespace NetCat.Network.Tun
{
    public class WintunManager
    {
        public static bool IsWintunInstalled()
        {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string[] possiblePaths =
            {
                Path.Combine(baseDir, "wintun.dll"),
                Path.Combine(baseDir, "bin", "sing-box", "wintun.dll"),
                Path.Combine(baseDir, "bin", "openvpn", "wintun.dll"),
                Path.Combine(Environment.SystemDirectory, "wintun.dll")
            };

            foreach (var p in possiblePaths)
            {
                if (File.Exists(p)) return true;
            }
            return false;
        }

        public static NetworkInterface? FindWintunInterface(string interfaceName = "wintun-netcat")
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.Name.Equals(interfaceName, StringComparison.OrdinalIgnoreCase) ||
                    ni.Description.Contains("Wintun", StringComparison.OrdinalIgnoreCase))
                {
                    return ni;
                }
            }
            return null;
        }
    }

    public class RouteTableManager
    {
        private static readonly List<string> TrackedRoutes = new();

        public static void AddRoute(string destination, string mask, string gateway, int metric = 1)
        {
            try
            {
                string cmd = $"route add {destination} mask {mask} {gateway} metric {metric}";
                ExecuteNetshOrRoute(cmd);
                lock (TrackedRoutes)
                {
                    TrackedRoutes.Add($"{destination} mask {mask} {gateway}");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to add route {destination}: {ex.Message}");
            }
        }

        public static void DeleteRoute(string destination, string mask, string gateway)
        {
            try
            {
                string cmd = $"route delete {destination} mask {mask} {gateway}";
                ExecuteNetshOrRoute(cmd);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to delete route {destination}: {ex.Message}");
            }
        }

        public static void ClearTrackedRoutes()
        {
            lock (TrackedRoutes)
            {
                foreach (var route in TrackedRoutes)
                {
                    try
                    {
                        ExecuteNetshOrRoute($"route delete {route}");
                    }
                    catch { }
                }
                TrackedRoutes.Clear();
            }
        }

        public static void FlushDns()
        {
            try
            {
                ExecuteNetshOrRoute("ipconfig /flushdns");
            }
            catch { }
        }

        private static void ExecuteNetshOrRoute(string arguments)
        {
            var psi = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c {arguments}",
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using var process = Process.Start(psi);
            process?.WaitForExit(3000);
        }
    }

    public class DnsLeakProtector
    {
        public static void EnableProtection()
        {
            // Set Smart Multi-Homed Name Resolution policy or preferred adapter DNS if necessary
            RouteTableManager.FlushDns();
        }

        public static void DisableProtection()
        {
            RouteTableManager.FlushDns();
        }
    }
}
