using System;
using System.IO;
using NetCat.Engine.Management;

namespace NetCat.Engine.TgWsProxy
{
    public class TgWsProxyProcess
    {
        private readonly ProcessSupervisor _supervisor;
        private readonly string _binaryPath;
        private const string ProcessKey = "tg-ws-proxy";

        public bool IsRunning => _supervisor.IsRunning(ProcessKey);

        public TgWsProxyProcess(ProcessSupervisor supervisor, string? binaryPath = null)
        {
            _supervisor = supervisor;
            _binaryPath = binaryPath ?? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "bin", "tg-ws-proxy", "TgWsProxy_windows.exe");
        }

        public void Start(int listenPort = 10852, string targetHost = "149.154.167.50")
        {
            // Flowseal tg-ws-proxy command line arguments
            string args = $"-l 127.0.0.1:{listenPort} -s {targetHost}";
            _supervisor.StartProcess(ProcessKey, _binaryPath, args);
        }

        public void Stop()
        {
            _supervisor.StopProcess(ProcessKey);
        }
    }
}
