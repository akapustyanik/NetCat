using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;

namespace NetCat.Engine;
public sealed class ProcessHost : IDisposable
{
    public static string PowerShellPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),"WindowsPowerShell","v1.0","powershell.exe");
    private Process? process;
    private SafeFileHandle job;
    private readonly object sync = new();
    private bool disposed, stopping;
    private Task readers = Task.CompletedTask;
    private readonly System.Collections.Concurrent.ConcurrentQueue<string> output = new();
    public event Action<string>? Line;
    public bool Running { get { lock(sync) return process is { HasExited: false }; } }
    public int Id { get { lock(sync) return process?.Id ?? 0; } }
    public string LastOutput => string.Join(Environment.NewLine, output);
    public ProcessHost() => job=NewJob();
    private static SafeFileHandle NewJob()
    {
        var job = CreateJobObject(IntPtr.Zero, null);
        var info = new JobInfo(); info.BasicLimitInformation.LimitFlags = 0x2000;
        if (job.IsInvalid || !SetInformationJobObject(job, 9, ref info, (uint)Marshal.SizeOf<JobInfo>())) { var error=new System.ComponentModel.Win32Exception(); job.Dispose(); throw error; }
        return job;
    }
    public void Start(string executable, IEnumerable<string> args, string? directory = null, bool log = true)
    {
        lock(sync)
        {
            ObjectDisposedException.ThrowIf(disposed,this);
            if (Running || stopping) throw new InvalidOperationException("Процесс уже запущен или останавливается.");
            job.Dispose(); job=NewJob();
            output.Clear(); process?.Dispose(); process=null;
            var started=OwnedProcess.Start(job,executable,args,directory); process=started.Process;
            async Task Read(StreamReader reader)
            {
                using(reader) while(await reader.ReadLineAsync().ConfigureAwait(false) is { } data)
                { output.Enqueue(Redact(data)); while(output.Count>12) output.TryDequeue(out _); Line?.Invoke(log ? Redact(data) : data); }
            }
            readers=Task.WhenAll(Task.Run(()=>Read(started.Output)),Task.Run(()=>Read(started.Error)));
        }
    }
    public async Task StopAsync()
    {
        Process? current;
        lock(sync) { if(disposed)return; if(!TerminateJobObject(job,1)) throw new System.ComponentModel.Win32Exception(); current=process; stopping=true; }
        try { if(current!=null) await current.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)); await readers.WaitAsync(TimeSpan.FromSeconds(10)); }
        finally { lock(sync) { if(ReferenceEquals(process,current)) { current?.Dispose(); process=null; } stopping=false; } }
    }
    public static async Task<(int Code, string Output)> RunAsync(string executable, IEnumerable<string> args, CancellationToken ct = default)
    {
        using var host=new ProcessHost(); var text=new System.Text.StringBuilder();
        host.Line+=line=> {lock(text) text.AppendLine(line);}; host.Start(executable,args,log:false);
        try
        {
            await host.process!.WaitForExitAsync(ct); var code=host.process.ExitCode;
            // One-shot commands must not retain detached descendants holding the output pipes.
            if(!TerminateJobObject(host.job,1)) throw new System.ComponentModel.Win32Exception();
            await host.readers.WaitAsync(ct); return(code,text.ToString());
        }
        catch { await host.StopAsync(); throw; }
    }
    public static string Redact(string text)
    {
        text = Regex.Replace(text, @"\x1B\[[0-?]*[ -/]*[@-~]", "");
        text = Regex.Replace(text, @"(?i)(https?|tg|vless|vmess|trojan|ss|hysteria2|tuic)://[^\s""']+", "[адрес скрыт]");
        if (Regex.IsMatch(text, "(?i)(password|secret|private.key|auth.token|uuid)")) return "[строка с параметрами авторизации скрыта]";
        return text;
    }
    public void Dispose() { lock(sync) { if(disposed)return; disposed=true; job.Dispose(); process?.Dispose(); process=null; } }
    [StructLayout(LayoutKind.Sequential)] private struct BasicLimits { public long PerProcessUserTimeLimit, PerJobUserTimeLimit; public uint LimitFlags; public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize; public uint ActiveProcessLimit; public UIntPtr Affinity; public uint PriorityClass, SchedulingClass; }
    [StructLayout(LayoutKind.Sequential)] private struct IoCounters { public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount; }
    [StructLayout(LayoutKind.Sequential)] private struct JobInfo { public BasicLimits BasicLimitInformation; public IoCounters IoInfo; public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafeFileHandle CreateJobObject(IntPtr attributes, string? name);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetInformationJobObject(SafeFileHandle job, int cls, ref JobInfo info, uint length);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool TerminateJobObject(SafeFileHandle job,uint code);
}
