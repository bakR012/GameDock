using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

// Release check: present real Direct3D frames using Microsoft's software
// renderer so this also runs on Windows build machines without a physical GPU.
internal static class FpsLiveCheck {
    sealed class TestCapture : IFpsCapture {
        readonly int pid;
        readonly bool fail;
        internal TestCapture(int pid,bool fail) {this.pid=pid;this.fail=fail;}
        public double Rate(long now) {return fail?0:pid;}
        public void Run(CancellationToken token) {
            if(fail) throw new InvalidOperationException("Transient test failure");
            token.WaitHandle.WaitOne();
        }
    }
    static void AwaitReading(FpsSession client,int expected,double rate) {
        var elapsed=Stopwatch.StartNew();
        while(elapsed.ElapsedMilliseconds<6500) {
            int state;
            if(client.Reading(out state)==rate && state==expected) return;
            Thread.Sleep(30);
        }
        throw new InvalidOperationException("Timed out waiting for FPS state "+expected+" at "+rate);
    }
    internal static void Recovery() {
        using(var client=new FpsSession(Guid.NewGuid().ToString("N"),true))
        using(var worker=new FpsSession(client.Id,false))
        using(var exit=new ManualResetEvent(false)) {
            int foreground=0,starts=0;
            var task=Task.Run(()=>worker.Serve(exit,()=>Volatile.Read(ref foreground),
                pid=>new TestCapture(pid,Interlocked.Increment(ref starts)==1)));
            try {
                client.TargetGame(60);AwaitReading(client,-3,0);
                Volatile.Write(ref foreground,60);
                AwaitReading(client,-1,0);AwaitReading(client,1,60);
                for(int i=0;i<6;i++) {Thread.Sleep(450);AwaitReading(client,1,60);}
                client.TargetGame(0);AwaitReading(client,1,0);
                Volatile.Write(ref foreground,30);client.TargetGame(30);AwaitReading(client,1,30);
                if(starts!=3) throw new InvalidOperationException("Unexpected capture restart");
            } finally {exit.Set();if(!task.Wait(5000)) throw new InvalidOperationException("FPS worker failed to stop");}
            Console.WriteLine("PASS FPS worker recovers a missed foreground and failed capture, stays active, idles and follows a renderer handoff");
        }
    }
    [StructLayout(LayoutKind.Sequential)]
    struct Mode {internal uint Width,Height,RefreshNumerator,RefreshDenominator,Format,Scanline,Scaling;}
    [StructLayout(LayoutKind.Sequential)]
    struct Description {
        internal Mode Mode;
        internal uint SampleCount,Quality,Usage,Buffers;
        internal IntPtr Window;
        internal int Windowed;
        internal uint SwapEffect,Flags;
    }
    [DllImport("d3d11.dll",ExactSpelling=true)]
    static extern int D3D11CreateDeviceAndSwapChain(IntPtr adapter,uint driver,IntPtr software,uint flags,
        IntPtr levels,uint count,uint sdk,ref Description description,out IntPtr chain,out IntPtr device,
        out uint level,out IntPtr context);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    delegate int Present(IntPtr chain,uint sync,uint flags);
    [DllImport("user32")] static extern IntPtr SendMessage(IntPtr window,uint message,IntPtr key,IntPtr flags);
    sealed class GameWindow : Form {
        System.Drawing.Rectangle restore;
        internal void PressF11() {SendMessage(Handle,0x100,(IntPtr)Keys.F11,IntPtr.Zero);}
        protected override void WndProc(ref Message msg) {
            if(msg.Msg==0x100 && msg.WParam==(IntPtr)Keys.F11) {
                if(FormBorderStyle!=FormBorderStyle.None) {
                    restore=Bounds;var monitor=Screen.FromHandle(Handle).Bounds;
                    FormBorderStyle=FormBorderStyle.None;Bounds=monitor;
                } else {FormBorderStyle=FormBorderStyle.Sizable;Bounds=restore;}
                return;
            }
            base.WndProc(ref msg);
        }
    }

    internal static void Run(bool replay=false) {
        int pid=Process.GetCurrentProcess().Id;
        var frames=new NativeFrames(pid);
        using(var stop=new CancellationTokenSource(TimeSpan.FromSeconds(25)))
        using(var form=new GameWindow {Text="GameDock FPS check",Width=420,Height=160,ShowInTaskbar=false}) {
            
            form.Show();
            var desc=new Description {Mode=new Mode {Width=96,Height=64,Format=28},SampleCount=1,
                Usage=32,Buffers=1,Window=form.Handle,Windowed=1};
            IntPtr chain,device,context; uint level;
            Marshal.ThrowExceptionForHR(D3D11CreateDeviceAndSwapChain(IntPtr.Zero,5,IntPtr.Zero,0,IntPtr.Zero,0,7,
                ref desc,out chain,out device,out level,out context));
            try {
                var method=Marshal.ReadIntPtr(Marshal.ReadIntPtr(chain),8*IntPtr.Size);
                var present=(Present)Marshal.GetDelegateForFunctionPointer(method,typeof(Present));
                var capture=Task.Run(()=>{if(replay) frames.TestPrivateTrace(stop.Token);else frames.Run(stop.Token);});
                double rate=0;long firstReading=0;bool verified=false;
                var elapsed=Stopwatch.StartNew();
                try {
                    while(!stop.IsCancellationRequested && !capture.IsCompleted) {
                        if(elapsed.ElapsedMilliseconds>=450 && form.FormBorderStyle!=FormBorderStyle.None) form.PressF11();
                        Marshal.ThrowExceptionForHR(present(chain,0,0));
                        Application.DoEvents(); Thread.Sleep(33);
                        rate=frames.Rate(Stopwatch.GetTimestamp());
                        if(rate>=5 && rate<=45) {
                            if(firstReading==0) firstReading=Stopwatch.GetTimestamp();
                            if(replay || Stopwatch.GetTimestamp()-firstReading>=4*Stopwatch.Frequency) {verified=true;break;}
                        } else firstReading=0;
                    }
                } finally {
                    stop.Cancel();
                    try {capture.GetAwaiter().GetResult();} catch(OperationCanceledException) {}
                }
                if(!verified) throw new InvalidOperationException("Direct3D FPS measurement did not complete: "+rate);
                Console.WriteLine("PASS real Direct3D presents measured with "+(replay?"private ETW playback":"live Windows ETW")+": "+rate.ToString("F1")+" FPS; capture stopped");
                if(form.Bounds!=Screen.FromHandle(form.Handle).Bounds) throw new InvalidOperationException("F11 did not cover the monitor");
                CheckOverlay(form.Handle,rate);
                var hud=new FpsHud();
                try {
                    var monitor=form.Bounds;
                    form.SetBounds(monitor.Left-160,monitor.Top-80,monitor.Width+320,monitor.Height+160);
                    foreach(bool right in new[]{false,true}) {
                        hud.Display(form.Handle,form.Handle,rate,right);
                        FpsWindows.Rect box;
                        if(!hud.IsVisible || !FpsWindows.GetWindowRect(new System.Windows.Interop.WindowInteropHelper(hud).Handle,out box) ||
                            box.Left<monitor.Left || box.Top<monitor.Top || box.Right>monitor.Right || box.Bottom>monitor.Bottom)
                            throw new InvalidOperationException("Counter outside the visible fullscreen monitor");
                    }
                    form.PressF11();hud.Display(form.Handle,form.Handle,rate,false);
                    if(!hud.IsVisible || form.FormBorderStyle==FormBorderStyle.None) throw new InvalidOperationException("Windowed return failed");
                    Console.WriteLine("PASS borderless F11 during D3D presentation, oversized viewport in both corners and windowed return");
                } finally {hud.Close();}
            } finally {Marshal.Release(context);Marshal.Release(device);Marshal.Release(chain);}
        }
    }
    static void CheckOverlay(IntPtr window,double rate) {
        var hud=new FpsHud();
        try {
            var before=FpsWindows.GetForegroundWindow();
            hud.Display(window,window,rate,false);
            if(!hud.IsVisible || FpsWindows.GetForegroundWindow()!=before)
                throw new InvalidOperationException("Showing FPS changed focus or left the overlay hidden");
            var row=(System.Windows.Controls.StackPanel)((System.Windows.Controls.Border)hud.Content).Child;
            if(((System.Windows.Controls.TextBlock)row.Children[0]).Text!="FPS" ||
                ((System.Windows.Controls.TextBlock)row.Children[1]).Text!=Math.Round(rate).ToString(System.Globalization.CultureInfo.InvariantCulture))
                throw new InvalidOperationException("FPS label or measured number is missing");
            var h=new System.Windows.Interop.WindowInteropHelper(hud).Handle;
            FpsWindows.Rect left,right;FpsWindows.GetWindowRect(h,out left);
            hud.Display(window,window,rate,true);FpsWindows.GetWindowRect(h,out right);
            if(right.Left<=left.Left) throw new InvalidOperationException("FPS corner did not change");
            Thread.Sleep(1100);
            FpsWindows.SetWindowPos(window,new IntPtr(-1),0,0,0,0,0x13);
            FpsWindows.SetWindowPos(h,new IntPtr(-2),0,0,0,0,0x13);
            if(FpsWindows.IsAbove(h,window)) throw new InvalidOperationException("Could not stage a covered overlay");
            hud.Display(window,window,rate,true);
            if(!FpsWindows.IsAbove(h,window)) throw new InvalidOperationException("Game still covers FPS");
            hud.Display(window,IntPtr.Zero,rate,true);
            if(hud.IsVisible) throw new InvalidOperationException("Overlay visible outside game");
            hud.Display(window,window,rate,false);
            if(!hud.IsVisible || !FpsWindows.IsAbove(h,window)) throw new InvalidOperationException("Overlay failed to return");
            Console.WriteLine("PASS HUD shows measured FPS in both corners, repairs Z order and returns after simulated focus loss");
        } finally {hud.Close();}
    }
}

// Runs only in the test executable. A private trace lets a standard user
// exercise the same event decoder against real frames from this test process.
internal sealed partial class NativeFrames {
    internal void TestPrivateTrace(CancellationToken token) {
        string dir=System.IO.Path.Combine(System.IO.Path.GetTempPath(),"GameDock-frame-test-"+Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(dir);
        string file=System.IO.Path.Combine(dir,"frames.etl"),name="GameDockFrameTest-"+Guid.NewGuid().ToString("N");
        int size=Marshal.SizeOf(typeof(Properties)),fileOffset=size+(name.Length+1)*2,total=fileOffset+(file.Length+80)*2;
        var props=Marshal.AllocHGlobal(total);ulong session=0;
        try {
            Marshal.Copy(new byte[total],0,props,total);
            Marshal.StructureToPtr(new Properties {Size=(uint)total,Clock=1,Flags=0x20000,Guid=Dxgi,BufferKb=64,
                Mode=0x800|0x20000|1,NameOffset=(uint)size,FileOffset=(uint)fileOffset},props,false);
            Marshal.Copy((name+"\0").ToCharArray(),0,IntPtr.Add(props,size),name.Length+1);
            Marshal.Copy((file+"\0").ToCharArray(),0,IntPtr.Add(props,fileOffset),file.Length+1);
            ulong h;uint err=StartTrace(out h,name,props);
            if(err!=0) throw new System.ComponentModel.Win32Exception((int)err,"Start private frame test: "+err);
            session=h;
            // This test already restricts collection to the current process.
            var options=new Parameters {Version=2};var provider=Dxgi;
            err=EnableTraceEx2(h,ref provider,1,5,0,0,0,ref options);
            if(err!=0) throw new System.ComponentModel.Win32Exception((int)err,"Enable private frame test: "+err);
            token.WaitHandle.WaitOne(1800);
            err=ControlTrace(h,null,props,1);if(err!=0) throw new System.ComponentModel.Win32Exception((int)err);
            session=0;
            foreach(string trace in System.IO.Directory.GetFiles(dir)) {
                IntPtr path=Marshal.StringToHGlobalUni(trace);Receive callback=Accept;
                try {
                    var log=new Log {File=path,Mode=0x10000000|0x1000,RecordCallback=Marshal.GetFunctionPointerForDelegate(callback)};
                    ulong reader=OpenTrace(ref log);
                    if(reader==UInt64.MaxValue || reader==UInt32.MaxValue) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
                    try {err=ProcessTrace(ref reader,1,IntPtr.Zero,IntPtr.Zero);if(err!=0) throw new System.ComponentModel.Win32Exception((int)err);}
                    finally {CloseTrace(reader);}
                } finally {Marshal.FreeHGlobal(path);GC.KeepAlive(callback);}
            }
            token.WaitHandle.WaitOne(1500);
        } finally {
            if(session!=0) ControlTrace(session,null,props,1);
            Marshal.FreeHGlobal(props);System.IO.Directory.Delete(dir,true);
        }
    }
}
