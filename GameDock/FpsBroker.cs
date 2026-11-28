using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Security.Principal;
using System.ServiceProcess;
using System.Threading;
using System.Threading.Tasks;

internal sealed class FpsBroker : IFpsCapture {
    internal const string Name="GameDockFrameMeter";
    internal const string Pipe="GameDockFrameMeter.v1";
    readonly int pid;
    long lastRate,lastReply;
    internal FpsBroker(int target) {pid=target;}
    internal static bool Installed() {
        try {using(var svc=new ServiceController(Name)) {var status=svc.Status;return true;}}
        catch(InvalidOperationException) {return false;} catch(Win32Exception) {return false;}
    }
    public double Rate(long now) {
        if(now-Interlocked.Read(ref lastReply)>3*Stopwatch.Frequency) return 0;
        return BitConverter.Int64BitsToDouble(Interlocked.Read(ref lastRate));
    }
    public void Run(CancellationToken token) {
        using(var svc=new ServiceController(Name)) {
            if(svc.Status==ServiceControllerStatus.StopPending) svc.WaitForStatus(ServiceControllerStatus.Stopped,TimeSpan.FromSeconds(8));
            svc.Refresh();
            if(svc.Status==ServiceControllerStatus.Stopped) {
                try {svc.Start();} catch(InvalidOperationException e) {
                    var w=e.InnerException as Win32Exception;if(w==null || w.NativeErrorCode!=1056) throw;
                }
            }
        }
        using(var pipe=new NamedPipeClientStream(".",Pipe,System.IO.Pipes.PipeAccessRights.ReadData|System.IO.Pipes.PipeAccessRights.WriteData,
            PipeOptions.Asynchronous,TokenImpersonationLevel.Identification,HandleInheritability.None))
        using(token.Register(()=>pipe.Dispose())) {
            pipe.Connect(5000);
            ReadSamples(pipe,token);
        }
    }
    internal void ReadSamples(Stream pipe,CancellationToken token) {
        var bytes=BitConverter.GetBytes(pid);pipe.Write(bytes,0,4);
        var sample=new byte[8];
        using(var timeout=CancellationTokenSource.CreateLinkedTokenSource(token)) {
            while(!token.IsCancellationRequested) {
                timeout.CancelAfter(8000);
                for(int n=0;n<sample.Length;) {
                    int read=pipe.ReadAsync(sample,n,sample.Length-n,timeout.Token).GetAwaiter().GetResult();
                    if(read==0) throw new EndOfStreamException();
                    n+=read;
                }
                double value=BitConverter.ToDouble(sample,0);
                if(Double.IsNaN(value) || Double.IsInfinity(value) || value<0) throw new IOException("Invalid frame sample");
                Interlocked.Exchange(ref lastRate,BitConverter.DoubleToInt64Bits(value));
                Interlocked.Exchange(ref lastReply,Stopwatch.GetTimestamp());
            }
        }
    }
}
