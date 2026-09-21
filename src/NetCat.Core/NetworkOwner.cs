using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
namespace NetCat.Core;

/// <summary>Machine ownership and same-session Show IPC are deliberately independent.</summary>
public sealed class NetworkOwner : IDisposable
{
    [StructLayout(LayoutKind.Sequential)] private struct SecurityAttributes {public int Size;public nint Descriptor;public int Inherit;}
    [DllImport("advapi32.dll",CharSet=CharSet.Unicode,SetLastError=true)] private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptor(string text,uint revision,out nint descriptor,out uint length);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)] private static extern nint CreateMutexEx(ref SecurityAttributes attributes,string name,uint flags,uint access);
    [DllImport("kernel32.dll")] private static extern nint LocalFree(nint memory);
    private readonly ManualResetEvent release=new(false);
    private readonly CancellationTokenSource lifetime=new();
    private readonly Thread owner;
    private Task? server;
    public bool Acquired {get;private set;}
    public static string ActivationName => "NetCat.Show."+Process.GetCurrentProcess().SessionId;
    public NetworkOwner(string name="Global\\NetCat.NetworkOwner")
    {
        // Mutex ownership is thread-affine, so keep it on one dedicated thread across async UI work.
        using var ready=new ManualResetEvent(false);Exception? failure=null;
        owner=new Thread(()=>
        {
            try
            {
                if(!ConvertStringSecurityDescriptorToSecurityDescriptor("D:P(A;;GA;;;SY)(A;;GA;;;BA)(A;;0x00100001;;;AU)",1,out var descriptor,out _)) throw new Win32Exception();
                nint handle;
                try {var attributes=new SecurityAttributes {Size=Marshal.SizeOf<SecurityAttributes>(),Descriptor=descriptor}; handle=CreateMutexEx(ref attributes,name,0,0x00100001);}
                finally {LocalFree(descriptor);}
                if(handle==0)throw new Win32Exception();
                using var mutex=new Mutex(); mutex.SafeWaitHandle=new SafeWaitHandle(handle,true);
                try {Acquired=mutex.WaitOne(0);}catch(AbandonedMutexException){Acquired=true;}
                ready.Set();
                if(Acquired) {release.WaitOne();mutex.ReleaseMutex();}
            }
            catch(Exception ex) {failure=ex;ready.Set();}
        }) {IsBackground=true,Name="NetCat network ownership"};
        owner.Start();ready.WaitOne();if(failure!=null)throw failure;
    }
    public void Listen(Func<Task> show,string? pipeName=null)
    {
        if(!Acquired || server!=null)throw new InvalidOperationException("Нет владения сетью.");
        server=Task.Run(async ()=>
        {
            while(!lifetime.IsCancellationRequested)
            {
                try
                {
                    using var pipe=new NamedPipeServerStream(pipeName ?? ActivationName,PipeDirection.InOut,1,PipeTransmissionMode.Byte,PipeOptions.Asynchronous|PipeOptions.CurrentUserOnly);
                    await pipe.WaitForConnectionAsync(lifetime.Token);
                    using var deadline=CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);deadline.CancelAfter(TimeSpan.FromSeconds(2));
                    var command=new byte[1];await pipe.ReadExactlyAsync(command,deadline.Token);
                    if(command[0]!=1)continue;
                    await show();await pipe.WriteAsync(new byte[]{1},deadline.Token);
                }
                catch(OperationCanceledException) { }
                catch(IOException) { }
            }
        });
    }
    public static async Task<bool> ShowExistingAsync(string? pipeName=null)
    {
        try
        {
            using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(2));
            using var pipe=new NamedPipeClientStream(".",pipeName ?? ActivationName,PipeDirection.InOut,PipeOptions.Asynchronous|PipeOptions.CurrentUserOnly);
            await pipe.ConnectAsync(deadline.Token);await pipe.WriteAsync(new byte[]{1},deadline.Token);
            var ack=new byte[1];await pipe.ReadExactlyAsync(ack,deadline.Token);return ack[0]==1;
        }
        catch(OperationCanceledException) {return false;}
        catch(IOException) {return false;}
        catch(UnauthorizedAccessException) {return false;}
    }
    public void Dispose() {lifetime.Cancel();release.Set();owner.Join();release.Dispose();}
}
