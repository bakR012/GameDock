using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

// Release check: present real Direct3D frames using Microsoft's software
// renderer so this also runs on Windows build machines without a physical GPU.
internal static class FpsLiveCheck {
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

    internal static void Run() {
        int pid=Process.GetCurrentProcess().Id;
        var frames=new FrameSamples(pid);
        using(var stop=new CancellationTokenSource(TimeSpan.FromSeconds(25)))
        using(var form=new Form {Text="GameDock FPS check",Width=140,Height=90,ShowInTaskbar=false}) {
            string tool=FpsCapture.GetTool(stop.Token);
            form.Show();
            var desc=new Description {Mode=new Mode {Width=96,Height=64,Format=28},SampleCount=1,
                Usage=32,Buffers=1,Window=form.Handle,Windowed=1};
            IntPtr chain,device,context; uint level;
            Marshal.ThrowExceptionForHR(D3D11CreateDeviceAndSwapChain(IntPtr.Zero,5,IntPtr.Zero,0,IntPtr.Zero,0,7,
                ref desc,out chain,out device,out level,out context));
            try {
                var method=Marshal.ReadIntPtr(Marshal.ReadIntPtr(chain),8*IntPtr.Size);
                var present=(Present)Marshal.GetDelegateForFunctionPointer(method,typeof(Present));
                var capture=Task.Run(()=>FpsCapture.Run(tool,pid,frames,stop.Token));
                double rate=0;
                try {
                    while(!stop.IsCancellationRequested && !capture.IsCompleted) {
                        Marshal.ThrowExceptionForHR(present(chain,0,0));
                        Application.DoEvents(); Thread.Sleep(33);
                        rate=frames.Rate(Stopwatch.GetTimestamp());
                        if(rate>=5 && rate<=45) break;
                    }
                } finally {
                    stop.Cancel();
                    try {capture.GetAwaiter().GetResult();} catch(OperationCanceledException) {}
                }
                if(rate<5 || rate>45) throw new InvalidOperationException("No valid Direct3D FPS reading: "+rate);
                Console.WriteLine("PASS real Direct3D presents measured with PresentMon: "+rate.ToString("F1")+" FPS; capture stopped");
            } finally {Marshal.Release(context);Marshal.Release(device);Marshal.Release(chain);}
        }
    }
}
