using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace NetCat.Engine;
internal static class OwnedProcess
{
    internal sealed record Started(Process Process, StreamReader Output, StreamReader Error);
    // Atomic job assignment closes the Process.Start -> AssignProcessToJobObject race.
    // No user code runs, and no unowned suspended process exists, even if NetCat dies here.
    internal static Started Start(SafeFileHandle job,string executable,IEnumerable<string> args,string? directory)
    {
        if(!Path.IsPathRooted(executable) && !File.Exists(executable))
            executable=(Environment.GetEnvironmentVariable("PATH") ?? "").Split(';').Select(p=>Path.Combine(p.Trim('"'),executable)).FirstOrDefault(File.Exists) ?? executable;
        if(!File.Exists(executable)) throw new FileNotFoundException("Модуль не установлен: "+Path.GetFileName(executable));
        executable=Path.GetFullPath(executable);
        var security=new Security {Length=Marshal.SizeOf<Security>(),Inherit=true};
        if(!CreatePipe(out var outputRead,out var outputWrite,ref security,0)) throw new Win32Exception();
        using(outputWrite) using(outputRead)
        {
            if(!CreatePipe(out var errorRead,out var errorWrite,ref security,0)) throw new Win32Exception();
            using(errorWrite) using(errorRead)
            using(var input=CreateFile("NUL",0x80000000,3,ref security,3,0,0))
            {
                if(input.IsInvalid || !SetHandleInformation(outputRead,1,0) || !SetHandleInformation(errorRead,1,0)) throw new Win32Exception();
                nuint size=0; InitializeProcThreadAttributeList(0,2,0,ref size);
                var attributes=Marshal.AllocHGlobal(checked((int)size)); var jobs=Marshal.AllocHGlobal(IntPtr.Size); var handles=Marshal.AllocHGlobal(IntPtr.Size*3);
                bool initialized=false; ProcessInfo created=default; Process? process=null; StreamReader? stdout=null,stderr=null;
                try
                {
                    if(!InitializeProcThreadAttributeList(attributes,2,0,ref size)) throw new Win32Exception(); initialized=true;
                    Marshal.WriteIntPtr(jobs,job.DangerousGetHandle());
                    Marshal.WriteIntPtr(handles,input.DangerousGetHandle()); Marshal.WriteIntPtr(handles,IntPtr.Size,outputWrite.DangerousGetHandle()); Marshal.WriteIntPtr(handles,IntPtr.Size*2,errorWrite.DangerousGetHandle());
                    if(!UpdateProcThreadAttribute(attributes,0,0x2000D,jobs,(nuint)IntPtr.Size,0,0) || !UpdateProcThreadAttribute(attributes,0,0x20002,handles,(nuint)(IntPtr.Size*3),0,0)) throw new Win32Exception();
                    var startup=new StartupEx {Info=new Startup {Size=Marshal.SizeOf<StartupEx>(),Flags=0x100,Input=input.DangerousGetHandle(),Output=outputWrite.DangerousGetHandle(),Error=errorWrite.DangerousGetHandle()},Attributes=attributes};
                    var command=new StringBuilder(string.Join(" ",new[]{executable}.Concat(args).Select(Quote)));
                    if(!CreateProcess(executable,command,0,0,true,0x08080004,0,directory ?? Path.GetDirectoryName(executable),ref startup,out created)) throw new Win32Exception();
                    process=Process.GetProcessById(created.ProcessId); _=process.Handle; process.EnableRaisingEvents=true;
                    // Duplicate read handles: local pipe handles are released on return.
                    stdout=Reader(outputRead); stderr=Reader(errorRead);
                    if(ResumeThread(created.Thread)==uint.MaxValue) throw new Win32Exception();
                    return new(process,stdout,stderr);
                }
                catch { if(created.Process!=0) TerminateProcess(created.Process,1); process?.Dispose(); stdout?.Dispose(); stderr?.Dispose(); throw; }
                finally
                {
                    if(created.Thread!=0) CloseHandle(created.Thread); if(created.Process!=0) CloseHandle(created.Process);
                    if(initialized) DeleteProcThreadAttributeList(attributes);
                    Marshal.FreeHGlobal(attributes); Marshal.FreeHGlobal(jobs); Marshal.FreeHGlobal(handles);
                }
            }
        }
    }
    private static StreamReader Reader(SafeFileHandle original)
    {
        var self=GetCurrentProcess(); if(!DuplicateHandle(self,original,self,out var copy,0,false,2)) throw new Win32Exception();
        try { return new StreamReader(new FileStream(copy,FileAccess.Read,4096,false),Encoding.UTF8,true); } catch { copy.Dispose(); throw; }
    }
    private static string Quote(string value)
    {
        var text=new StringBuilder("\""); int slashes=0;
        foreach(char c in value)
        {
            if(c=='\\') { slashes++; continue; }
            text.Append('\\',c=='"' ? slashes*2+1 : slashes); text.Append(c); slashes=0;
        }
        return text.Append('\\',slashes*2).Append('"').ToString();
    }
    [StructLayout(LayoutKind.Sequential)] private struct Security {public int Length; public nint Descriptor; [MarshalAs(UnmanagedType.Bool)] public bool Inherit;}
    [StructLayout(LayoutKind.Sequential)] private struct Startup {public int Size; public nint Reserved,Desktop,Title; public uint X,Y,Width,Height,XChars,YChars,Fill,Flags; public ushort Show,ReservedSize; public nint ReservedBytes,Input,Output,Error;}
    [StructLayout(LayoutKind.Sequential)] private struct StartupEx {public Startup Info; public nint Attributes;}
    [StructLayout(LayoutKind.Sequential)] private struct ProcessInfo {public nint Process,Thread; public int ProcessId,ThreadId;}
    [DllImport("kernel32.dll",SetLastError=true)] private static extern bool CreatePipe(out SafeFileHandle read,out SafeFileHandle write,ref Security security,uint size);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true,EntryPoint="CreateFileW")] private static extern SafeFileHandle CreateFile(string path,uint access,uint share,ref Security security,uint creation,uint flags,nint template);
    [DllImport("kernel32.dll",SetLastError=true)] private static extern bool SetHandleInformation(SafeFileHandle handle,uint mask,uint flags);
    [DllImport("kernel32.dll",SetLastError=true)] private static extern bool InitializeProcThreadAttributeList(nint list,int count,int flags,ref nuint size);
    [DllImport("kernel32.dll",SetLastError=true)] private static extern bool UpdateProcThreadAttribute(nint list,uint flags,nuint attribute,nint value,nuint size,nint previous,nint returned);
    [DllImport("kernel32.dll")] private static extern void DeleteProcThreadAttributeList(nint list);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true,EntryPoint="CreateProcessW")] private static extern bool CreateProcess(string application,StringBuilder command,nint processSecurity,nint threadSecurity,bool inherit,uint flags,nint environment,string? directory,ref StartupEx startup,out ProcessInfo process);
    [DllImport("kernel32.dll",SetLastError=true)] private static extern uint ResumeThread(nint thread);
    [DllImport("kernel32.dll")] private static extern bool TerminateProcess(nint process,uint code);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(nint handle);
    [DllImport("kernel32.dll")] private static extern nint GetCurrentProcess();
    [DllImport("kernel32.dll",SetLastError=true)] private static extern bool DuplicateHandle(nint source,SafeFileHandle handle,nint target,out SafeFileHandle duplicate,uint access,bool inherit,uint options);
}
