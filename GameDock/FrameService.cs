using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.ServiceProcess;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

internal sealed class FrameService : ServiceBase {
    [DllImport("kernel32")] static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe,out int pid);
    [DllImport("kernel32")] static extern bool ProcessIdToSessionId(int pid,out int session);
    readonly CancellationTokenSource shutdown=new CancellationTokenSource();
    int clients;
    internal static void Main() {Run(new FrameService {ServiceName=FpsBroker.Name,AutoLog=false});}
    protected override void OnStart(string[] args) {Task.Run((Func<Task>)Listen);}
    protected override void OnStop() {
        shutdown.Cancel();
        SpinWait.SpinUntil(()=>Volatile.Read(ref clients)==0,6000);
    }
    static NamedPipeServerStream Listener() {
        var acl=new PipeSecurity();
        acl.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid,null),PipeAccessRights.FullControl,AccessControlType.Deny));
        acl.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalServiceSid,null),PipeAccessRights.FullControl,AccessControlType.Allow));
        acl.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.InteractiveSid,null),PipeAccessRights.ReadWrite,AccessControlType.Allow));
        return new NamedPipeServerStream(FpsBroker.Pipe,PipeDirection.InOut,8,PipeTransmissionMode.Byte,PipeOptions.Asynchronous,256,256,acl);
    }
    async Task Listen() {
        try {
            while(!shutdown.IsCancellationRequested) {
                var pipe=Listener();
                try {
                    var connected=pipe.WaitForConnectionAsync();
                    while(await Task.WhenAny(connected,Task.Delay(15000,shutdown.Token))!=connected) {
                        if(shutdown.IsCancellationRequested) return;
                        if(Volatile.Read(ref clients)==0) {Stop();return;}
                    }
                    await connected;
                    if(Volatile.Read(ref clients)>=6) continue;
                    var connection=pipe;Interlocked.Increment(ref clients);
                    Handle(connection);
                    pipe=null;
                } finally {if(pipe!=null) pipe.Dispose();}
            }
        } catch(Exception) {if(!shutdown.IsCancellationRequested) Stop();}
    }
    async void Handle(NamedPipeServerStream connection) {
        try {await Measure(connection,shutdown.Token,pid=>new NativeFrames(pid));} catch(Exception) {}
        finally {connection.Dispose();Interlocked.Decrement(ref clients);}
    }
    internal static async Task Measure(NamedPipeServerStream pipe,CancellationToken stop,Func<int,IFpsCapture> create) {
        using(var deadline=CancellationTokenSource.CreateLinkedTokenSource(stop))
        using(deadline.Token.Register(()=>pipe.Dispose())) {
            deadline.CancelAfter(5000);
            int client; if(!GetNamedPipeClientProcessId(pipe.SafePipeHandle,out client)) return;
            var request=new byte[4];
            for(int n=0;n<4;) {int got=await pipe.ReadAsync(request,n,4-n,deadline.Token);if(got==0)return;n+=got;}
            int pid=BitConverter.ToInt32(request,0);
            using(var capture=CancellationTokenSource.CreateLinkedTokenSource(stop)) {
                int session,targetSession;
                if(!ProcessIdToSessionId(client,out session) || session==0 ||
                    !ProcessIdToSessionId(pid,out targetSession) || session!=targetSession) return;
                var meter=create(pid);
                var trace=Task.Run(()=>meter.Run(capture.Token));
                try {
                    while(!trace.IsCompleted && ProcessIdToSessionId(pid,out targetSession) && targetSession==session) {
                        var reply=BitConverter.GetBytes(meter.Rate(Stopwatch.GetTimestamp()));
                        deadline.CancelAfter(4000);
                        await pipe.WriteAsync(reply,0,reply.Length,deadline.Token);
                        await Task.Delay(450,deadline.Token);
                    }
                } finally {capture.Cancel();trace.GetAwaiter().GetResult();}
            }
        }
    }
}
