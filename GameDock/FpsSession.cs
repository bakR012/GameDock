using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;

// Only numeric target, generation, status and rate cross the privilege boundary.
// The worker has no command for file access, launching programs or writing settings.
internal sealed class FpsSession : IDisposable {
    const int Target=0,Generation=4,Reply=8,Status=12,Rate=16;
    internal readonly string Id;
    readonly MemoryMappedFile map;
    readonly MemoryMappedViewAccessor values;
    readonly Mutex gate;
    readonly EventWaitHandle changed;
    int requested;
    static string Name(string id) {return @"Local\GameDockFPS-"+id;}
    static void Access(ObjectSecurity acl) {
        foreach(var sid in new[]{WindowsIdentity.GetCurrent().User,new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid,null)}) {
            var m=acl as MemoryMappedFileSecurity;var g=acl as MutexSecurity;var e=acl as EventWaitHandleSecurity;
            if(m!=null) m.AddAccessRule(new AccessRule<MemoryMappedFileRights>(sid,MemoryMappedFileRights.FullControl,AccessControlType.Allow));
            if(g!=null) g.AddAccessRule(new MutexAccessRule(sid,MutexRights.FullControl,AccessControlType.Allow));
            if(e!=null) e.AddAccessRule(new EventWaitHandleAccessRule(sid,EventWaitHandleRights.FullControl,AccessControlType.Allow));
        }
    }
    internal FpsSession(string id,bool create) {
        Id=id;string prefix=Name(id);
        try {
            if(create) {
                var ma=new MemoryMappedFileSecurity();var mu=new MutexSecurity();var ev=new EventWaitHandleSecurity();
                Access(ma);Access(mu);Access(ev);bool fresh;
                map=MemoryMappedFile.CreateNew(prefix+"-data",32,MemoryMappedFileAccess.ReadWrite,MemoryMappedFileOptions.None,ma,HandleInheritability.None);
                gate=new Mutex(false,prefix+"-lock",out fresh,mu);
                changed=new EventWaitHandle(false,EventResetMode.AutoReset,prefix+"-wake",out fresh,ev);
            } else {
                map=MemoryMappedFile.OpenExisting(prefix+"-data",MemoryMappedFileRights.ReadWrite);
                gate=Mutex.OpenExisting(prefix+"-lock",MutexRights.Synchronize|MutexRights.Modify);
                changed=EventWaitHandle.OpenExisting(prefix+"-wake",EventWaitHandleRights.Synchronize|EventWaitHandleRights.Modify);
            }
            values=map.CreateViewAccessor();
        } catch {Dispose();throw;}
    }
    void Lock() {try {gate.WaitOne();} catch(AbandonedMutexException) {}}
    internal void TargetGame(int pid) {
        Lock();
        try {values.Write(Target,pid);values.Write(Generation,++requested);} finally {gate.ReleaseMutex();}
        changed.Set();
    }
    internal double Reading(out int state) {
        Lock();
        try {
            if(values.ReadInt32(Reply)!=requested) {state=0;return 0;}
            state=values.ReadInt32(Status);
            return Stopwatch.GetTimestamp()-values.ReadInt64(24)>3*Stopwatch.Frequency?0:values.ReadDouble(Rate);
        } finally {gate.ReleaseMutex();}
    }
    void Publish(int generation,int state,double rate) {
        Lock();
        try {values.Write(Rate,rate);values.Write(24,Stopwatch.GetTimestamp());values.Write(Status,state);values.Write(Reply,generation);}
        finally {gate.ReleaseMutex();}
    }
    internal static bool NeedsConsent() {
        using(var identity=WindowsIdentity.GetCurrent()) {
            var user=new WindowsPrincipal(identity);
            return !user.IsInRole(WindowsBuiltInRole.Administrator) && !user.IsInRole(new SecurityIdentifier("S-1-5-32-559"));
        }
    }
    internal Process StartWorker(bool elevate) {
        using(var parent=Process.GetCurrentProcess()) {
            var info=new ProcessStartInfo(typeof(FpsSession).Assembly.Location,
                "--fps-session "+Id+" "+parent.Id+" "+parent.StartTime.ToUniversalTime().Ticks);
            info.UseShellExecute=elevate;info.CreateNoWindow=true;info.WindowStyle=ProcessWindowStyle.Hidden;
            info.WorkingDirectory=Environment.SystemDirectory;
            if(elevate) info.Verb="runas";
            return Process.Start(info);
        }
    }
    internal static void Worker(string[] args) {
        Guid id;int parentId;long started;
        if(args.Length!=4 || !Guid.TryParseExact(args[1],"N",out id) || !Int32.TryParse(args[2],out parentId) || !Int64.TryParse(args[3],out started)) return;
        try {
            using(var parent=Process.GetProcessById(parentId))
            using(var self=Process.GetCurrentProcess())
            using(var exit=new ManualResetEvent(false)) {
                if(parent.SessionId!=self.SessionId || parent.StartTime.ToUniversalTime().Ticks!=started ||
                    !String.Equals(parent.MainModule.FileName,self.MainModule.FileName,StringComparison.OrdinalIgnoreCase)) return;
                parent.Exited+=delegate {try {exit.Set();} catch(ObjectDisposedException) {}};
                parent.EnableRaisingEvents=true;
                using(var session=new FpsSession(id.ToString("N"),false)) session.Serve(exit);
            }
        } catch(Exception) { /* Parent closed or Windows refused access. */ }
    }
    void Serve(WaitHandle exit) {
        int current=-1;NativeFrames meter=null;CancellationTokenSource stop=null;Task capture=null;
        var waits=new[]{exit,changed};
        try {
            for(;;) {
                int pid,generation;
                Lock();try {pid=values.ReadInt32(Target);generation=values.ReadInt32(Generation);} finally {gate.ReleaseMutex();}
                if(pid<0 || exit.WaitOne(0)) break;
                if(current!=generation) {
                    Finish(ref stop,ref capture);meter=null;current=generation;
                    int foreground=FpsWindows.RenderingPid(FpsWindows.GetForegroundWindow());
                    if(pid>0 && pid==foreground) {
                        meter=new NativeFrames(pid);stop=new CancellationTokenSource();
                        var token=stop.Token;var active=meter;
                        capture=Task.Run(()=>active.Run(token));
                    }
                }
                int state=pid>0 && meter==null?-3:1;double rate=0;
                if(capture!=null && capture.IsCompleted) {
                    var ex=capture.IsFaulted?capture.Exception.GetBaseException() as Win32Exception:null;
                    state=ex!=null && ex.NativeErrorCode==5?-2:-1;
                    Finish(ref stop,ref capture);
                } else if(meter!=null) rate=meter.Rate(Stopwatch.GetTimestamp());
                Publish(current,state,rate);
                if(WaitHandle.WaitAny(waits,capture==null?Timeout.Infinite:450)==0) break;
            }
        } finally {Finish(ref stop,ref capture);}
    }
    static void Finish(ref CancellationTokenSource stop,ref Task capture) {
        if(stop==null) return;
        stop.Cancel();
        try {capture.GetAwaiter().GetResult();} catch(Exception) {}
        stop.Dispose();stop=null;capture=null;
    }
    public void Dispose() {
        if(values!=null) values.Dispose();if(map!=null) map.Dispose();if(gate!=null) gate.Dispose();if(changed!=null) changed.Dispose();
    }
}
