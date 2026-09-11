using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;

namespace NetCat.Engine.Management
{
    public class ProcessSupervisor : IDisposable
    {
        private readonly ConcurrentDictionary<string, Process> _runningProcesses = new();
        private IntPtr _jobHandle = IntPtr.Zero;
        private bool _disposed;

        public event EventHandler<(string ProcessName, string Message)>? OutputReceived;
        public event EventHandler<(string ProcessName, string Error)>? ErrorReceived;
        public event EventHandler<(string ProcessName, int ExitCode)>? ProcessExited;

        public ProcessSupervisor()
        {
            InitializeJobObject();
            AppDomain.CurrentDomain.ProcessExit += (s, e) => TerminateAll();
        }

        private void InitializeJobObject()
        {
            try
            {
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    _jobHandle = CreateJobObject(IntPtr.Zero, null);
                    var info = new JOBOBJECT_BASIC_LIMIT_INFORMATION
                    {
                        LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
                    };
                    var extendedInfo = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
                    {
                        BasicLimitInformation = info
                    };

                    int length = Marshal.SizeOf(typeof(JOBOBJECT_EXTENDED_LIMIT_INFORMATION));
                    IntPtr extendedInfoPtr = Marshal.AllocHGlobal(length);
                    try
                    {
                        Marshal.StructureToPtr(extendedInfo, extendedInfoPtr, false);
                        SetInformationJobObject(_jobHandle, JobObjectInfoType.ExtendedLimitInformation, extendedInfoPtr, (uint)length);
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(extendedInfoPtr);
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to initialize Job Object: {ex.Message}");
            }
        }

        public Process StartProcess(string key, string executablePath, string arguments, string? workingDirectory = null)
        {
            StopProcess(key);

            if (!File.Exists(executablePath))
            {
                throw new FileNotFoundException($"Binary executable not found: {executablePath}");
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = executablePath,
                Arguments = arguments,
                WorkingDirectory = workingDirectory ?? Path.GetDirectoryName(executablePath) ?? AppDomain.CurrentDomain.BaseDirectory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };

            process.OutputDataReceived += (s, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data))
                    OutputReceived?.Invoke(this, (key, e.Data));
            };

            process.ErrorDataReceived += (s, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data))
                    ErrorReceived?.Invoke(this, (key, e.Data));
            };

            process.Exited += (s, e) =>
            {
                int exitCode = -1;
                try { exitCode = process.ExitCode; } catch { }
                _runningProcesses.TryRemove(key, out _);
                ProcessExited?.Invoke(this, (key, exitCode));
            };

            if (!process.Start())
            {
                throw new InvalidOperationException($"Failed to start process '{key}': {executablePath}");
            }

            if (_jobHandle != IntPtr.Zero && RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                try
                {
                    AssignProcessToJobObject(_jobHandle, process.Handle);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Failed to assign process to JobObject: {ex.Message}");
                }
            }

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            _runningProcesses[key] = process;
            return process;
        }

        public bool IsRunning(string key)
        {
            if (_runningProcesses.TryGetValue(key, out var proc))
            {
                try { return !proc.HasExited; } catch { return false; }
            }
            return false;
        }

        public void StopProcess(string key)
        {
            if (_runningProcesses.TryRemove(key, out var proc))
            {
                try
                {
                    if (!proc.HasExited)
                    {
                        proc.Kill(true);
                        proc.WaitForExit(2000);
                    }
                }
                catch { }
                finally
                {
                    proc.Dispose();
                }
            }
        }

        public void TerminateAll()
        {
            foreach (var key in _runningProcesses.Keys)
            {
                StopProcess(key);
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            TerminateAll();
            if (_jobHandle != IntPtr.Zero)
            {
                CloseHandle(_jobHandle);
                _jobHandle = IntPtr.Zero;
            }
        }

        #region P/Invoke Win32 Job Object
        private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateJobObject(IntPtr lpJobAttributes, string? lpName);

        [DllImport("kernel32.dll")]
        private static extern bool SetInformationJobObject(IntPtr hJob, JobObjectInfoType infoType, IntPtr lpJobObjectInfo, uint cbJobObjectInfoLength);

        [DllImport("kernel32.dll")]
        private static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);

        private enum JobObjectInfoType
        {
            ExtendedLimitInformation = 9
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
        {
            public long PerProcessUserTimeLimit;
            public long PerJobUserTimeLimit;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize;
            public UIntPtr MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint PriorityClass;
            public uint SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct IO_COUNTERS
        {
            public ulong ReadOperationCount;
            public ulong WriteOperationCount;
            public ulong OtherOperationCount;
            public ulong ReadTransferCount;
            public ulong WriteTransferCount;
            public ulong OtherTransferCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
            public IO_COUNTERS IoInfo;
            public UIntPtr ProcessMemoryLimit;
            public UIntPtr JobMemoryLimit;
            public UIntPtr PeakProcessMemoryLimit;
            public UIntPtr PeakJobMemoryLimit;
        }
        #endregion
    }

    public static class PortAllocator
    {
        public static int FindAvailableTcpPort(int preferredPort = 0)
        {
            if (preferredPort > 0 && IsPortAvailable(preferredPort))
            {
                return preferredPort;
            }

            var listener = new TcpListener(IPAddress.Loopback, 0);
            try
            {
                listener.Start();
                return ((IPEndPoint)listener.LocalEndpoint).Port;
            }
            finally
            {
                listener.Stop();
            }
        }

        public static bool IsPortAvailable(int port)
        {
            try
            {
                var listener = new TcpListener(IPAddress.Loopback, port);
                listener.Start();
                listener.Stop();
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}
