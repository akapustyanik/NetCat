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
    private int disposed;
    private readonly object listenerGate = new();
    public Action<Exception>? ListenerError { get; set; }
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
    public void Listen(
        Func<Task> show,
        string? pipeName = null)
        => Listen(show, null, pipeName);

    public void Listen(
        Func<Task> show,
        Func<Task>? exit,
        string? pipeName = null)
    {
        lock (listenerGate)
        {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        if (!Acquired || server != null)
            throw new InvalidOperationException(
                "Нет владения сетью.");

        server = Task.Run(async () =>
        {
            var consecutiveFailures = 0;
            Task? pendingShow = null;
            Task? pendingExit = null;

            while (!lifetime.IsCancellationRequested)
            {
                try
                {
                    using var pipe = new NamedPipeServerStream(
                        pipeName ?? ActivationName,
                        PipeDirection.InOut,
                        1,
                        PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous |
                        PipeOptions.CurrentUserOnly);

                    await pipe.WaitForConnectionAsync(
                        lifetime.Token).ConfigureAwait(false);

                    using var deadline =
                        CancellationTokenSource.CreateLinkedTokenSource(
                            lifetime.Token);

                    deadline.CancelAfter(
                        TimeSpan.FromSeconds(5));

                    var command = new byte[1];

                    await pipe.ReadExactlyAsync(
                        command,
                        deadline.Token).ConfigureAwait(false);

                    if (command[0] == 1)
                    {
                        // A faulty or blocked UI callback must not
                        // permanently terminate the IPC listener.
                        // A timeout only stops waiting; it cannot cancel an arbitrary
                        // callback. Reuse it until completion instead of launching
                        // overlapping UI operations on every subsequent request.
                        if (pendingShow == null || pendingShow.IsCompleted)
                        {
                            pendingShow = show();
                            ObserveCallback(pendingShow);
                        }
                        await pendingShow.WaitAsync(
                            deadline.Token).ConfigureAwait(false);

                        await pipe.WriteAsync(
                            new byte[] { 1 },
                            deadline.Token).ConfigureAwait(false);
                    }
                    else if (command[0] == 2 && exit != null)
                    {
                        await pipe.WriteAsync(
                            new byte[] { 2 },
                            deadline.Token).ConfigureAwait(false);

                        if (pendingExit == null || pendingExit.IsCompleted)
                        {
                            pendingExit = exit();
                            ObserveCallback(pendingExit);
                        }
                        await pendingExit.WaitAsync(deadline.Token).ConfigureAwait(false);
                    }

                    consecutiveFailures = 0;
                }
                catch (OperationCanceledException)
                    when (lifetime.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception error)
                {
                    if (lifetime.IsCancellationRequested)
                        break;

                    // Reporting must not be able to kill the server.
                    try
                    {
                        ListenerError?.Invoke(error);
                    }
                    catch
                    {
                    }

                    // Back off on persistent pipe/handler failures.
                    // An access-denied or broken pipe must not
                    // produce an unbounded tight retry loop.
                    consecutiveFailures = Math.Min(
                        consecutiveFailures + 1,
                        6);

                    var delay = Math.Min(
                        3200,
                        100 * (1 << (consecutiveFailures - 1)));

                    try
                    {
                        await Task.Delay(
                            TimeSpan.FromMilliseconds(delay),
                            lifetime.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                }
            }
        });
        }
    }
    private static void ObserveCallback(Task callback) => _ = callback.ContinueWith(
        completed => { _ = completed.Exception; }, CancellationToken.None,
        TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
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
    public static async Task<bool> ExitExistingAsync(string? pipeName=null)
    {
        try
        {
            using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var pipe=new NamedPipeClientStream(".",pipeName ?? ActivationName,PipeDirection.InOut,PipeOptions.Asynchronous|PipeOptions.CurrentUserOnly);
            await pipe.ConnectAsync(deadline.Token);await pipe.WriteAsync(new byte[]{2},deadline.Token);
            var ack=new byte[1];await pipe.ReadExactlyAsync(ack,deadline.Token);return ack[0]==2;
        }
        catch(OperationCanceledException) {return false;}
        catch(IOException) {return false;}
        catch(UnauthorizedAccessException) {return false;}
    }
    public void Dispose()
    {
        lock (listenerGate)
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            lifetime.Cancel();
            release.Set();
            owner.Join();
            release.Dispose();
            Acquired = false;
            if (server == null) lifetime.Dispose();
            else _ = server.ContinueWith(_ => lifetime.Dispose(), CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
    }
}
