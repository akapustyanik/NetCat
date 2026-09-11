using System;
using System.IO;
using NetCat.Engine.Management;

namespace NetCat.Engine.Zapret
{
    public class ZapretArgsBuilder
    {
        public static string BuildArgs(string strategy = "ALT13", string customArgs = "")
        {
            if (!string.IsNullOrWhiteSpace(customArgs))
            {
                return customArgs;
            }

            switch (strategy.ToUpperInvariant())
            {
                case "ALT13":
                    // Flowseal zapret-discord-youtube ALT13 strategy (google ip_id + fake for others)
                    return "--wf-tcp=80,443 --wf-udp=443 --filter-tcp=80,443 --dpi-desync=fake,split2 --dpi-desync-autottl=2 --dpi-desync-fooling=md5sig --filter-udp=443 --dpi-desync=fake --dpi-desync-any-protocol";

                case "DISCORD":
                    return "--wf-tcp=443 --wf-udp=443 --filter-tcp=443 --dpi-desync=split --dpi-desync-split-pos=1 --filter-udp=443 --dpi-desync=fake";

                case "YOUTUBE":
                    return "--wf-tcp=443 --filter-tcp=443 --dpi-desync=fake --dpi-desync-fooling=badseq";

                default:
                    return "--wf-tcp=80,443 --wf-udp=443 --filter-tcp=80,443 --dpi-desync=fake,multisplit --dpi-desync-split-pos=1";
            }
        }
    }

    public class ZapretProcess
    {
        private readonly ProcessSupervisor _supervisor;
        private readonly string _binaryPath;
        private const string ProcessKey = "zapret-winws";

        public bool IsRunning => _supervisor.IsRunning(ProcessKey);

        public ZapretProcess(ProcessSupervisor supervisor, string? binaryPath = null)
        {
            _supervisor = supervisor;
            _binaryPath = binaryPath ?? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "bin", "zapret", "winws.exe");
        }

        public void Start(string strategy = "ALT13", string customArgs = "")
        {
            string args = ZapretArgsBuilder.BuildArgs(strategy, customArgs);
            _supervisor.StartProcess(ProcessKey, _binaryPath, args);
        }

        public void Stop()
        {
            _supervisor.StopProcess(ProcessKey);
        }
    }
}
