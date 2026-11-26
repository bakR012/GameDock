using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;

// ETW records the game's presents, independently of the dock's render loop.
internal sealed class FrameSamples {
    sealed class Chain {internal readonly Queue<long> Frames=new Queue<long>(); internal long Last;}
    readonly Dictionary<long,Chain> chains=new Dictionary<long,Chain>();
    internal readonly int Pid;
    internal FrameSamples(int pid) {Pid=pid;}
    internal void Add(long chainId,long stamp) {
        lock(chains) {
            Chain chain;
            if(!chains.TryGetValue(chainId,out chain)) {
                if(chains.Count>=8) chains.Remove(chains.OrderBy(p=>p.Value.Last).First().Key);
                chains[chainId]=chain=new Chain();
            }
            if(stamp<=chain.Last) return;
            while(chain.Frames.Count>=4096 || chain.Frames.Count>1 && stamp-chain.Frames.Peek()>Stopwatch.Frequency)
                chain.Frames.Dequeue();
            chain.Frames.Enqueue(stamp); chain.Last=stamp;
        }
    }
    internal double Rate(long now) {
        lock(chains) {
            Chain best=null;
            foreach(var c in chains.Values)
                if(c.Frames.Count>=2 && now-c.Last<=3*Stopwatch.Frequency && (best==null || best.Frames.Count<c.Frames.Count)) best=c;
            return best==null?0:(best.Frames.Count-1)*(double)Stopwatch.Frequency/(best.Last-best.Frames.Peek());
        }
    }
}

internal interface IFpsCapture {
    double Rate(long now);
    void Run(CancellationToken token);
}

internal sealed partial class NativeFrames : IFpsCapture {
    double IFpsCapture.Rate(long now) {return Rate(now);}
    void IFpsCapture.Run(CancellationToken token) {Run(token);}
    static readonly Guid Dxgi=new Guid("ca11c036-0102-4a2d-a6ad-f03cfed5d3c9");
    static readonly Guid D3d9=new Guid("783aca0a-790e-4d7f-8451-aa850511c6b9");
    static readonly Guid KernelGraphics=new Guid("802ec45a-1e99-4b83-9920-87c98277ba9d");
    [StructLayout(LayoutKind.Sequential)] struct Properties {
        internal uint Size,Provider; internal ulong Historical,Timestamp; internal Guid Guid; internal uint Clock,Flags;
        internal uint BufferKb,Min,Max,FileSize,Mode,Flush,Enable,Age,Buffers,Free,Lost,Written,LogLost,RealtimeLost;
        internal IntPtr Thread; internal uint FileOffset,NameOffset;
    }
    [StructLayout(LayoutKind.Sequential)] struct Filter {internal ulong Address;internal uint Size,Type;}
    [StructLayout(LayoutKind.Sequential)] struct Parameters {
        internal uint Version,Property,Control;internal Guid Source;internal IntPtr Filters;internal uint Count;
    }
    [StructLayout(LayoutKind.Sequential,Size=48)] struct ClassicHeader {internal long Alignment;}
    [StructLayout(LayoutKind.Sequential)] struct ClassicEvent {
        internal ClassicHeader Header; internal uint Instance,Parent;internal Guid ParentGuid;internal IntPtr Data;internal uint Length,Context;
    }
    [StructLayout(LayoutKind.Sequential,Size=172)] struct TimeZone {internal int Bias;}
    [StructLayout(LayoutKind.Sequential)] struct TraceHeader {
        internal uint Buffer,Version,Provider,Processors;internal long End;internal uint Resolution,FileSize,Mode,Written;
        internal Guid Instance;internal IntPtr Logger,File;internal TimeZone Zone;internal long Boot,Frequency,Start;
        internal uint Flags,Lost;
    }
    [StructLayout(LayoutKind.Sequential)] struct Log {
        internal IntPtr File,Name;internal long Now;internal uint Read,Mode;internal ClassicEvent Current;
        internal TraceHeader Header;internal IntPtr BufferCallback;internal uint BufferSize,Filled,Lost;
        internal IntPtr RecordCallback;internal uint Kernel;internal IntPtr Context;
    }
    [StructLayout(LayoutKind.Sequential)] internal struct Record {
        internal ushort Size,Type,Flags,Property;internal uint Thread,Pid; internal long Stamp;internal Guid Provider;
        internal ushort Id;internal byte Version,Channel,Level,Opcode;internal ushort Task;internal ulong Keyword,ProcessorTime;
        internal Guid Activity;internal uint BufferContext;internal ushort ExtendedCount,Length;internal IntPtr Extended,Data,Context;
    }
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] delegate void Receive(ref Record record);
    [DllImport("advapi32",CharSet=CharSet.Unicode)] static extern uint StartTrace(out ulong session,string name,IntPtr properties);
    [DllImport("advapi32",CharSet=CharSet.Unicode)] static extern uint ControlTrace(ulong session,string name,IntPtr properties,uint control);
    [DllImport("advapi32")] static extern uint EnableTraceEx2(ulong session,ref Guid provider,uint enable,byte level,ulong any,ulong all,uint timeout,ref Parameters parameters);
    [DllImport("advapi32",CharSet=CharSet.Unicode,SetLastError=true)] static extern ulong OpenTrace(ref Log log);
    [DllImport("advapi32")] static extern uint ProcessTrace(ref ulong handle,uint count,IntPtr start,IntPtr end);
    [DllImport("advapi32")] static extern uint CloseTrace(ulong handle);
    [StructLayout(LayoutKind.Sequential)] struct PropertyName {internal long Name;internal uint Index,Reserved;}
    [DllImport("tdh")] static extern uint TdhGetProperty(ref Record r,uint contextCount,IntPtr context,uint propertyCount,ref PropertyName name,uint size,out long value);
    readonly FrameSamples runtime,kernel;
    readonly Dictionary<long,long> pending=new Dictionary<long,long>();
    long lastRuntime;
    IntPtr windowProperty,statusProperty;
    internal NativeFrames(int pid) {runtime=new FrameSamples(pid);kernel=new FrameSamples(pid);}
    internal double Rate(long now) {double result=runtime.Rate(now);return result>0?result:kernel.Rate(now);}
    internal void Accept(ref Record r) {
        if(r.Pid!=runtime.Pid || r.Stamp<=0) return;
        bool legacy=r.Provider==D3d9;
        if(r.Provider==Dxgi || legacy) {
            int width=(r.Flags&0x20)!=0?4:8;
            long key=((long)r.Thread<<1)|(legacy?1L:0L);
            if(legacy?r.Id==1:r.Id==42 || r.Id==55) {
                pending.Remove(key);
                if(r.Data==IntPtr.Zero || r.Length<width+4 || (Marshal.ReadInt32(r.Data,width)&(legacy?4:1))!=0) return;
                if(pending.Count>=128) pending.Clear();
                pending[key]=width==4?(long)(uint)Marshal.ReadInt32(r.Data):Marshal.ReadInt64(r.Data);
            } else if(legacy?r.Id==2:r.Id==43 || r.Id==56) {
                long chain;
                if(!pending.TryGetValue(key,out chain)) return;
                pending.Remove(key);
                if(r.Length>=4 && r.Data!=IntPtr.Zero && Marshal.ReadInt32(r.Data)==0) {
                    runtime.Add(chain,r.Stamp);lastRuntime=r.Stamp;
                }
            }
        } else if(r.Provider==KernelGraphics && r.Id==184 && windowProperty!=IntPtr.Zero &&
            (lastRuntime==0 || r.Stamp-lastRuntime>3*Stopwatch.Frequency)) {
            var name=new PropertyName {Name=windowProperty.ToInt64(),Index=UInt32.MaxValue};
            var status=new PropertyName {Name=statusProperty.ToInt64(),Index=UInt32.MaxValue};
            long window,result;
            if(TdhGetProperty(ref r,0,IntPtr.Zero,1,ref status,4,out result)==0 && (int)result!=0) return;
            if(TdhGetProperty(ref r,0,IntPtr.Zero,1,ref name,8,out window)==0 && window!=0)
                kernel.Add(window,r.Stamp);
        }
    }
    static uint Enable(ulong session,Guid provider,int pid,ushort[] ids,ulong keywords) {
        int stride=Marshal.SizeOf(typeof(Filter)),length=4+ids.Length*2;
        IntPtr mem=Marshal.AllocHGlobal(stride*2+4+length);
        try {
            var target=IntPtr.Add(mem,stride*2);var events=IntPtr.Add(target,4);
            Marshal.WriteInt32(target,pid);Marshal.WriteInt32(events,(ids.Length<<16)|1);
            for(int i=0;i<ids.Length;i++) Marshal.WriteInt16(events,4+2*i,(short)ids[i]);
            Marshal.StructureToPtr(new Filter {Address=(ulong)target.ToInt64(),Size=4,Type=0x80000004},mem,false);
            Marshal.StructureToPtr(new Filter {Address=(ulong)events.ToInt64(),Size=(uint)length,Type=0x80000200},IntPtr.Add(mem,stride),false);
            var options=new Parameters {Version=2,Filters=mem,Count=2};
            uint err=EnableTraceEx2(session,ref provider,1,5,keywords,0,0,ref options);
            if(err==87) {
                // Some providers reject the combined filters. Retain the process
                // scope and let Accept discard unrelated events for that PID.
                options.Count=1;err=EnableTraceEx2(session,ref provider,1,5,keywords,0,0,ref options);
            }
            return err;
        } finally {Marshal.FreeHGlobal(mem);}
    }
    internal void Run(CancellationToken token) {
        string name="GameDock-FPS-"+Guid.NewGuid().ToString("N");
        int size=Marshal.SizeOf(typeof(Properties)),total=size+(name.Length+1)*2;
        IntPtr props=Marshal.AllocHGlobal(total),namePtr=Marshal.StringToHGlobalUni(name);
        ulong session=0,consumer=UInt64.MaxValue;
        Receive callback=Accept;
        try {
            token.ThrowIfCancellationRequested();
            Marshal.Copy(new byte[total],0,props,total);
            var settings=new Properties {Size=(uint)total,Clock=1,Flags=0x20000,BufferKb=32,Min=4,Max=16,
                Mode=0x100|0x10000000,Flush=1,NameOffset=(uint)size};
            Marshal.StructureToPtr(settings,props,false);
            Marshal.Copy((name+"\0").ToCharArray(),0,IntPtr.Add(props,size),name.Length+1);
            ulong handle;
            uint err=StartTrace(out handle,name,props);
            if(err!=0) throw new Win32Exception((int)err);
            session=handle;
            windowProperty=Marshal.StringToHGlobalUni("hWindow");
            statusProperty=Marshal.StringToHGlobalUni("ReturnStatus");
            var log=new Log {Name=namePtr,Mode=0x100|0x10000000|0x1000,RecordCallback=Marshal.GetFunctionPointerForDelegate(callback)};
            consumer=OpenTrace(ref log);
            if(consumer==UInt64.MaxValue || consumer==UInt32.MaxValue) throw new Win32Exception(Marshal.GetLastWin32Error());
            uint dxgi=Enable(session,Dxgi,runtime.Pid,new ushort[]{42,43,55,56},0);
            uint legacy=Enable(session,D3d9,runtime.Pid,new ushort[]{1,2},0);
            uint graphics=Enable(session,KernelGraphics,runtime.Pid,new ushort[]{184},1);
            if(dxgi!=0 && legacy!=0 && graphics!=0) throw new Win32Exception((int)dxgi);
            using(token.Register(delegate {ControlTrace(session,null,props,1);})) {
                var active=consumer;
                err=ProcessTrace(ref active,1,IntPtr.Zero,IntPtr.Zero);
                if(err!=0 && !token.IsCancellationRequested) throw new Win32Exception((int)err);
            }
        } finally {
            if(session!=0) ControlTrace(session,null,props,1);
            if(consumer!=UInt64.MaxValue && consumer!=UInt32.MaxValue) CloseTrace(consumer);
            if(windowProperty!=IntPtr.Zero) Marshal.FreeHGlobal(windowProperty);
            if(statusProperty!=IntPtr.Zero) Marshal.FreeHGlobal(statusProperty);
            windowProperty=IntPtr.Zero; Marshal.FreeHGlobal(props);Marshal.FreeHGlobal(namePtr);GC.KeepAlive(callback);
        }
    }
}
