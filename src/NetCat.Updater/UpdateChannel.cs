using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using NetCat.Core;

namespace NetCat.Updater;
public static class UpdateChannel
{
    [DllImport("kernel32.dll", SetLastError=true)] private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint pid);
    [DllImport("advapi32.dll", SetLastError=true)] private static extern bool OpenProcessToken(nint process, uint access, out SafeAccessTokenHandle token);
    [DllImport("advapi32.dll", SetLastError=true)] private static extern bool GetTokenInformation(SafeAccessTokenHandle token, int kind, out int value, int size, out int returned);
    [DllImport("kernel32.dll", SetLastError=true)] private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint pid);
    private static string SecureRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),"NetCat","updates");
    private static byte[] Hash(string path) { using var file=File.OpenRead(path); return SHA256.HashData(file); }
    private static string Stage(string path) => UpdateCleanup.ValidateStage(path);
    public static async Task LaunchAsync(string jobPath)
    {
        PublisherTrust.RequireSamePublisher(Environment.ProcessPath!,Environment.ProcessPath!);
        var stage=Stage(Path.GetDirectoryName(Path.GetFullPath(jobPath))!);
        if(Path.GetFileName(jobPath)!="job.json") throw new InvalidDataException("Недопустимое задание обновления.");
        var job=JsonSerializer.Deserialize<UpdateJob>(await File.ReadAllTextAsync(jobPath),JsonSettings.Options) ?? throw new InvalidDataException("Нет задания.");
        if(job.Root!=AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar) && Path.GetFullPath(job.Root).TrimEnd(Path.DirectorySeparatorChar)!=Path.GetFullPath(AppContext.BaseDirectory).TrimEnd(Path.DirectorySeparatorChar)) throw new InvalidDataException("Задание относится к другой копии NetCat.");
        if(job.Stage!=stage || job.ParentId!=Environment.ProcessId || job.ParentStart!=Process.GetCurrentProcess().StartTime.ToUniversalTime().Ticks) throw new InvalidDataException("Задание устарело.");
        var helper=Path.Combine(stage,"NetCat.Update.exe"); PublisherTrust.RequireSamePublisher(Environment.ProcessPath!,helper);
        UpdateAuthentication.HelperHash(Hash(Environment.ProcessPath!),Hash(helper));
        var name="NetCat.Update."+Guid.NewGuid().ToString("N");
        using var pipe=new NamedPipeServerStream(name,PipeDirection.InOut,1,PipeTransmissionMode.Byte,PipeOptions.Asynchronous|PipeOptions.CurrentUserOnly);
        using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var start=new ProcessStartInfo(helper) {UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden};
        start.ArgumentList.Add("--apply-update"); start.ArgumentList.Add(name);
        using var process=Process.Start(start) ?? throw new IOException("Не удалось запустить помощник.");
        await pipe.WaitForConnectionAsync(deadline.Token);
        if(!GetNamedPipeClientProcessId(pipe.SafePipeHandle,out var receiver)) throw new InvalidDataException("Не определён получатель задания.");
        UpdateAuthentication.Receiver(process.Id,checked((int)receiver));
        using var writer=new StreamWriter(pipe,leaveOpen:true) {AutoFlush=true}; using var reader=new StreamReader(pipe,leaveOpen:true);
        await writer.WriteLineAsync(JsonSerializer.Serialize(job).AsMemory(),deadline.Token);
        if(await reader.ReadLineAsync(deadline.Token)!="ready") throw new IOException("Помощник отклонил задание.");
    }
    public static async Task ReceiveAndApplyAsync(string pipeName)
    {
        // No JSON file supplied on the command line is ever opened by this entry point.
        UpdateAuthentication.PipeName(pipeName);
        PublisherTrust.RequireSamePublisher(Environment.ProcessPath!,Environment.ProcessPath!);
        var stage=Stage(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar));
        using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var pipe=new NamedPipeClientStream(".",pipeName,PipeDirection.InOut,PipeOptions.Asynchronous|PipeOptions.CurrentUserOnly);
        await pipe.ConnectAsync(deadline.Token);
        if(!GetNamedPipeServerProcessId(pipe.SafePipeHandle,out var pid)) throw new InvalidDataException("Не определён отправитель задания.");
        using var parent=Process.GetProcessById(checked((int)pid));
        if(!OpenProcessToken(parent.Handle,8,out var token)) throw new InvalidDataException("Не определены права отправителя.");
        using(token) if(!GetTokenInformation(token,20,out int elevated,sizeof(int),out _) || elevated==0) throw new InvalidDataException("Отправитель не является привилегированной копией NetCat.");
        var original=parent.MainModule?.FileName ?? throw new InvalidDataException("Нет пути отправителя.");
        PublisherTrust.RequireSamePublisher(Environment.ProcessPath!,original);
        UpdateAuthentication.HelperHash(Hash(original),Hash(Environment.ProcessPath!));
        using var reader=new StreamReader(pipe,leaveOpen:true); using var writer=new StreamWriter(pipe,leaveOpen:true) {AutoFlush=true};
        var line=await reader.ReadLineAsync(deadline.Token) ?? throw new InvalidDataException("Пустое задание.");
        if(line.Length>65536) throw new InvalidDataException("Слишком большое задание.");
        var job=JsonSerializer.Deserialize<UpdateJob>(line) ?? throw new InvalidDataException("Пустое задание.");
        var root=Path.GetDirectoryName(original)!;
        UpdateAuthentication.Job(job,new(parent.Id,parent.StartTime.ToUniversalTime().Ticks,original,true,Hash(original)),stage,Hash(Environment.ProcessPath!));
        await writer.WriteLineAsync("ready".AsMemory(),deadline.Token);
        await PortableUpdate.ApplyAuthenticatedJobAsync(job with {Root=root,Stage=stage});
    }
}
