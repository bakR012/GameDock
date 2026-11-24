using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;

internal static class FpsWindows {
    internal delegate void FocusEvent(IntPtr hook,uint ev,IntPtr window,int obj,int child,uint thread,uint time);
    [DllImport("user32")] internal static extern IntPtr SetWinEventHook(uint first,uint last,IntPtr module,FocusEvent callback,uint pid,uint thread,uint flags);
    [DllImport("user32")] internal static extern bool UnhookWinEvent(IntPtr hook);
    [DllImport("user32")] internal static extern IntPtr GetForegroundWindow();
    [DllImport("user32")] internal static extern uint GetWindowThreadProcessId(IntPtr hwnd,out int pid);
    [DllImport("user32",CharSet=CharSet.Unicode)] static extern int GetClassName(IntPtr window,StringBuilder name,int length);
    delegate bool ChildCallback(IntPtr window,IntPtr unused);
    [DllImport("user32")] static extern bool EnumChildWindows(IntPtr window,ChildCallback callback,IntPtr unused);
    internal static int RenderingPid(IntPtr window) {
        int owner;GetWindowThreadProcessId(window,out owner);
        if(owner==0) return 0;
        var name=new StringBuilder(96);GetClassName(window,name,name.Capacity);
        if(name.ToString()!="ApplicationFrameWindow") return owner;
        int result=owner;
        EnumChildWindows(window,delegate(IntPtr child,IntPtr unused) {
            name.Clear();GetClassName(child,name,name.Capacity);
            if(name.ToString()!="Windows.UI.Core.CoreWindow") return true;
            int pid;GetWindowThreadProcessId(child,out pid);
            if(pid==0 || pid==owner) return true;
            result=pid;return false;
        },IntPtr.Zero);
        return result;
    }
    [DllImport("user32",CharSet=CharSet.Unicode)] internal static extern int GetWindowText(IntPtr hwnd,StringBuilder buffer,int size);
    [DllImport("kernel32")] static extern IntPtr OpenProcess(uint access,bool inherit,int pid);
    [DllImport("kernel32",CharSet=CharSet.Unicode)] static extern bool QueryFullProcessImageName(IntPtr process,uint flags,StringBuilder path,ref int size);
    [DllImport("kernel32")] static extern bool CloseHandle(IntPtr handle);
    internal static string ImagePath(int pid) {
        var h=OpenProcess(0x1000,false,pid);
        if(h==IntPtr.Zero) return null;
        try {
            int length=512; var path=new StringBuilder(length);
            if(QueryFullProcessImageName(h,0,path,ref length)) return path.ToString();
            length=32768;path.EnsureCapacity(length);
            return QueryFullProcessImageName(h,0,path,ref length)?path.ToString():null;
        } finally {CloseHandle(h);}
    }
    [StructLayout(LayoutKind.Sequential)] internal struct Point {internal int X,Y;}
    [StructLayout(LayoutKind.Sequential)] internal struct Rect {internal int Left,Top,Right,Bottom;}
    [DllImport("user32")] internal static extern bool ClientToScreen(IntPtr h,ref Point p);
    [DllImport("user32")] internal static extern bool GetClientRect(IntPtr h,out Rect r);
    [DllImport("user32")] internal static extern bool GetWindowRect(IntPtr h,out Rect r);
    [DllImport("user32",EntryPoint="GetWindowLongW")] internal static extern int Style(IntPtr h,int index);
    [DllImport("user32",EntryPoint="SetWindowLongW")] internal static extern int Style(IntPtr h,int index,int value);
    [DllImport("user32")] internal static extern bool SetWindowPos(IntPtr h,IntPtr after,int x,int y,int w,int height,uint flags);
}

internal sealed class FpsLaunch {
    internal readonly string PathHint,Title;
    internal readonly int RootPid;
    internal readonly long Birth;
    readonly string directory;
    internal FpsLaunch(string path,string title,Process root) {
        PathHint=path;Title=GameArtwork.Normalize(title);
        if(!String.IsNullOrEmpty(path) && Path.IsPathRooted(path) && path.EndsWith(".exe",StringComparison.OrdinalIgnoreCase) &&
            !GameScan.IsLauncher(path)) directory=Path.GetDirectoryName(path);
        if(root!=null) try {RootPid=root.Id;Birth=root.StartTime.ToUniversalTime().Ticks;} catch(Exception) {}
    }
    internal bool Matches(string path,string title,int pid,Lazy<Dictionary<uint,uint>> ancestry=null) {
        if(String.Equals(path,PathHint,StringComparison.OrdinalIgnoreCase)) return true;
        if(Title.Length>=3 && (GameArtwork.Normalize(title)==Title || GameArtwork.Normalize(Path.GetFileNameWithoutExtension(path))==Title)) return true;
        if(!String.IsNullOrEmpty(directory) && path.StartsWith(directory+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase) &&
            path.IndexOf(@"\Binaries\",directory.Length,StringComparison.OrdinalIgnoreCase)>=0 &&
            path.EndsWith("-Shipping.exe",StringComparison.OrdinalIgnoreCase)) return true;
        return RootPid!=0 && Birth!=0 && Descendant(pid,ancestry);
    }
    [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)] struct Entry {
        internal uint Size,Usage,Pid; internal UIntPtr Heap;internal uint Module,Threads,Parent;internal int Priority;internal uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr,SizeConst=260)] internal string Exe;
    }
    [DllImport("kernel32")] static extern IntPtr CreateToolhelp32Snapshot(uint flags,uint pid);
    [DllImport("kernel32",CharSet=CharSet.Unicode)] static extern bool Process32First(IntPtr snapshot,ref Entry item);
    [DllImport("kernel32",CharSet=CharSet.Unicode)] static extern bool Process32Next(IntPtr snapshot,ref Entry item);
    [DllImport("kernel32")] static extern bool CloseHandle(IntPtr h);
    internal static Dictionary<uint,uint> Parents() {
        var parents=new Dictionary<uint,uint>();var snapshot=CreateToolhelp32Snapshot(2,0);
        if(snapshot==new IntPtr(-1)) return parents;
        try {
            var item=new Entry {Size=(uint)Marshal.SizeOf(typeof(Entry))};
            if(Process32First(snapshot,ref item)) do {parents[item.Pid]=item.Parent;} while(Process32Next(snapshot,ref item));
        } finally {CloseHandle(snapshot);}
        return parents;
    }
    bool Descendant(int pid,Lazy<Dictionary<uint,uint>> ancestry) {
        try {
            using(var game=Process.GetProcessById(pid)) if(game.StartTime.ToUniversalTime().Ticks<Birth) return false;
            try {using(var root=Process.GetProcessById(RootPid)) if(root.StartTime.ToUniversalTime().Ticks!=Birth) return false;}
            catch(ArgumentException) {} // Some bootstrappers exit as soon as their child opens.
            if(pid==RootPid) return true;
            var parents=ancestry==null?Parents():ancestry.Value;
            uint id=(uint)pid;
            for(int i=0;i<24 && parents.TryGetValue(id,out id);i++) if(id==(uint)RootPid) return true;
        } catch(ArgumentException) {} catch(InvalidOperationException) {} catch(System.ComponentModel.Win32Exception) {}
        return false;
    }
}

partial class GameDock {
    bool fpsEnabled=Preference("FpsEnabled",0,0,1)==1;
    int fpsCorner=Preference("FpsCorner",0,0,1);
    readonly string[] fpsLegacy=ReadSetting(prefs,"FpsGames",null) as string[] ?? new string[0];
    readonly List<FpsLaunch> fpsLaunches=new List<FpsLaunch>();
    FpsWindows.FocusEvent fpsCallback;
    IntPtr fpsHook,fpsWindow;
    int fpsPid;
    long fpsOpened;
    string fpsFailure;
    FpsHud fpsHud;
    DispatcherTimer fpsTimer;
    FpsSession fpsSession;
    Process fpsProcess;
    bool fpsStarting,fpsDeclined,fpsForceConsent;
    int fpsEpoch;
    void StartFps() {
        if(fpsTimer!=null) return;
        fpsTimer=new DispatcherTimer {Interval=TimeSpan.FromMilliseconds(500)};
        fpsTimer.Tick+=PaintFps;
        fpsCallback=delegate {if(!closing && fpsEnabled) FocusFps();};
        fpsHook=FpsWindows.SetWinEventHook(3,3,IntPtr.Zero,fpsCallback,0,0,0);
        if(fpsHook==IntPtr.Zero) {fpsFailure=T("تعذّر تتبّع نافذة اللعبة.","Windows could not track the foreground game.");return;}
        FocusFps();
    }
    async void PrepareFps(bool interactive) {
        if(!fpsEnabled || closing || fpsStarting || fpsSession!=null || fpsDeclined) return;
        bool elevate=fpsForceConsent || FpsSession.NeedsConsent();
        if(elevate && !interactive) return;
        fpsStarting=true;int epoch=fpsEpoch;FpsSession channel=null;
        try {
            channel=new FpsSession(Guid.NewGuid().ToString("N"),true);
            var worker=await Task.Run(()=>channel.StartWorker(elevate));
            if(worker==null) throw new IOException();
            if(!fpsEnabled || closing || epoch!=fpsEpoch) {channel.TargetGame(-1);worker.Dispose();return;}
            fpsSession=channel;channel=null;fpsProcess=worker;
            fpsProcess.Exited+=delegate {Dispatcher.BeginInvoke(new Action(delegate {
                if(fpsProcess!=worker || !fpsEnabled || closing) return;
                fpsFailure=T("توقف عدّاد FPS. أطفئ الخيار وفعّله لإعادة المحاولة.","FPS stopped. Switch it off and on to retry.");
                CloseFpsSession();fpsDeclined=true;RefreshFpsSettings();
            }));};
            fpsProcess.EnableRaisingEvents=true;
            FocusFps(); fpsSession.TargetGame(fpsPid);
        } catch(Exception) {
            if(!closing && fpsEnabled && epoch==fpsEpoch) {
                fpsDeclined=true;fpsFailure=T("يحتاج العدّاد إذن قياس الأداء من ويندوز. أطفئ الخيار وفعّله لإعادة المحاولة.","Windows performance access is required. Switch FPS off and on to try again.");
                RefreshFpsSettings();
            }
        } finally {
            fpsStarting=false;if(channel!=null) channel.Dispose();
            if(!closing && fpsEnabled && epoch!=fpsEpoch) PrepareFps(true);
        }
    }
    void RefreshFpsSettings() {if(settingsWindow!=null && settingsPage==2 && !minecraftDetails) DrawSettings();}
    void RememberFpsLaunch(string path,string title,Process root) {
        if(!fpsEnabled || closing) return;
        fpsLaunches.RemoveAll(l=>Paths.Equals(l.PathHint,path) && l.Title==GameArtwork.Normalize(title));
        fpsLaunches.Insert(0,new FpsLaunch(path,title,root));
        if(fpsLaunches.Count>12) fpsLaunches.RemoveAt(12);
        PrepareFps(true);FocusFps();
    }
    static bool FpsLaunchTarget(string target) {return !String.IsNullOrEmpty(target) && (target.EndsWith(".exe",StringComparison.OrdinalIgnoreCase) || target.StartsWith("steam://",StringComparison.OrdinalIgnoreCase) || target.StartsWith("com.epicgames.launcher:",StringComparison.OrdinalIgnoreCase) || target.StartsWith("uplay:",StringComparison.OrdinalIgnoreCase));}
    bool FpsGame(int pid,IntPtr window) {
        var path=FpsWindows.ImagePath(pid);
        var name=new StringBuilder(256);FpsWindows.GetWindowText(window,name,name.Capacity);
        string title=name.ToString();
        if(String.IsNullOrEmpty(path)) {
            string key=GameArtwork.Normalize(title);
            return key.Length>=3 && (fpsLaunches.Any(l=>l.Title==key) || detected.Games.Values.Any(g=>GameArtwork.Normalize(g.Title)==key));
        }
        if(GameScan.IsLauncher(path) || Paths.Equals(path,typeof(GameDock).Assembly.Location)) return false;
        string file=Path.GetFileNameWithoutExtension(path);
        if(NonGameProcesses.Contains(file)) return false;
        if(fpsLegacy.Contains(path,Paths) || detected.Games.Values.Any(g=>Paths.Equals(path,g.Proof) || Paths.Equals(path,g.Target))) return true;
        if(!automatic && manualGames.Values.Any(g=>!g.LauncherOnly && Paths.Equals(path,g.Target))) return true;
        if((Paths.Equals(file,"javaw") || Paths.Equals(file,"java")) &&
            System.Text.RegularExpressions.Regex.IsMatch(title,@"^Minecraft(?:$|[\s*:-])",System.Text.RegularExpressions.RegexOptions.IgnoreCase)) return true;
        var ancestry=new Lazy<Dictionary<uint,uint>>(FpsLaunch.Parents);
        return fpsLaunches.Any(l=>l.Matches(path,title,pid,ancestry));
    }
    static readonly HashSet<string> NonGameProcesses=new HashSet<string>(new[]{"explorer","chrome","msedge","brave","firefox","discord","steamwebhelper","msedgewebview2","riotclientux","leagueclientux","gamebar"},StringComparer.OrdinalIgnoreCase);
    void FocusFps() {
        if(fpsTimer==null || !fpsEnabled || closing) return;
        var window=FpsWindows.GetForegroundWindow();int pid=FpsWindows.RenderingPid(window);
        if(pid!=0 && pid==fpsPid) {fpsWindow=window;return;}
        if(pid!=0 && !FpsGame(pid,window)) pid=0;
        if(pid==fpsPid) return;
        fpsPid=pid;fpsWindow=window;fpsOpened=Stopwatch.GetTimestamp();
        if(fpsHud!=null) fpsHud.Hide();
        if(fpsSession!=null) fpsSession.TargetGame(pid);
        if(pid==0) {fpsTimer.Stop();return;}
        PrepareFps(false);fpsTimer.Start();PaintFps(null,EventArgs.Empty);
    }
    void PaintFps(object sender,EventArgs e) {
        if(fpsPid==0 || closing) return;
        if(fpsWindow!=FpsWindows.GetForegroundWindow()) {FocusFps();return;}
        int state=0;double rate=fpsSession==null?0:fpsSession.Reading(out state);
        if(state==-3 && fpsSession!=null) {fpsSession.TargetGame(fpsPid);return;}
        if(state==-2) fpsForceConsent=true;
        if(state<0) fpsFailure=state==-2?T("أطفئ خيار FPS وفعّله ثم وافق على إذن ويندوز للقياس.","Switch FPS off and on, then allow Windows performance access."):
            T("تعذّر بدء القياس في ويندوز. أطفئ الخيار وفعّله للمحاولة مجددًا.","Windows could not start capture. Switch FPS off and on to retry.");
        else if(rate>0) fpsFailure=null;
        else if(state==1 && Stopwatch.GetTimestamp()-fpsOpened>6*Stopwatch.Frequency)
            fpsFailure=T("لم تصل إطارات من نافذة اللعبة. جرّب وضع ملء الشاشة بلا حدود.","No frames received from this window. Try borderless fullscreen.");
        if(fpsHud==null) fpsHud=new FpsHud();
        fpsHud.Display(fpsWindow,rate,fpsCorner==1);
    }
    void CloseFpsSession() {
        if(fpsSession!=null) {fpsSession.TargetGame(-1);fpsSession.Dispose();fpsSession=null;}
        if(fpsProcess!=null) {fpsProcess.Dispose();fpsProcess=null;}
    }
    void StopFps() {
        fpsEpoch++;
        if(fpsHook!=IntPtr.Zero) FpsWindows.UnhookWinEvent(fpsHook);
        fpsHook=IntPtr.Zero;fpsCallback=null;fpsPid=0;fpsLaunches.Clear();
        if(fpsTimer!=null) {fpsTimer.Stop();fpsTimer=null;}
        if(fpsHud!=null) {fpsHud.Close();fpsHud=null;}
        CloseFpsSession();
    }
    void DrawFpsSettings(StackPanel body) {
        AddSwitch(body,T("إظهار FPS","Show FPS"),fpsEnabled,delegate(bool on) {
            fpsEnabled=on;SavePreference("FpsEnabled",on?1:0);fpsFailure=null;fpsDeclined=false;
            if(on) {StartFps();PrepareFps(true);} else StopFps();
            DrawSettings();
        });
        if(!fpsEnabled) return;
        Detail(body,fpsFailure ?? T("افتح لعبتك من الدوك بوضع النافذة أو ملء الشاشة بلا حدود. إذا طلب ويندوز إذن القياس، وافق لهذه الجلسة.","Launch a windowed or borderless game from the dock. If Windows asks, allow performance measurement for this session."));
        Section(body,T("مكان العدّاد","Counter position"),new[]{T("أعلى اليسار","Top left"),T("أعلى اليمين","Top right")},fpsCorner,
            delegate(int i){fpsCorner=i;SavePreference("FpsCorner",i);});
    }
}

internal sealed class FpsHud : Window {
    readonly TextBlock number=new TextBlock {FontSize=17,FontWeight=FontWeights.SemiBold,Foreground=Brushes.White,VerticalAlignment=VerticalAlignment.Center};
    internal FpsHud() {
        Title="GameDock FPS"; WindowStyle=WindowStyle.None; AllowsTransparency=true;
        Background=Brushes.Transparent; ResizeMode=ResizeMode.NoResize; ShowInTaskbar=false;
        ShowActivated=false; Topmost=true; Focusable=false; IsHitTestVisible=false;
        Width=88; Height=32; WindowStartupLocation=WindowStartupLocation.Manual;
        var row=new StackPanel {Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Center};
        row.FlowDirection=FlowDirection.LeftToRight;
        row.Children.Add(new TextBlock {Text="FPS",FontSize=10,FontWeight=FontWeights.Bold,Foreground=Brushes.PaleTurquoise,
            VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(0,1,6,0)});
        row.Children.Add(number);
        Content=new Border {CornerRadius=new CornerRadius(7),Background=new SolidColorBrush(Color.FromArgb(220,20,24,30)),Child=row};
        SourceInitialized+=delegate {
            var h=new WindowInteropHelper(this).Handle;
            FpsWindows.Style(h,-20,(FpsWindows.Style(h,-20)&~0x40000)|0x08000000|0x80|0x20);
        };
    }
    internal void Display(IntPtr game,double rate,bool right) {
        FpsWindows.Rect client,outer; var origin=new FpsWindows.Point();
        if(game!=FpsWindows.GetForegroundWindow() || !FpsWindows.GetClientRect(game,out client) ||
            !FpsWindows.ClientToScreen(game,ref origin) || client.Right<100 || client.Bottom<45) {Hide();return;}
        number.Text=rate<=0?"—":rate<1?rate.ToString("0.0",CultureInfo.InvariantCulture):Math.Round(rate).ToString(CultureInfo.InvariantCulture);
        if(!IsVisible) {Opacity=0;Show();}
        var handle=new WindowInteropHelper(this).Handle;
        if(!FpsWindows.GetWindowRect(handle,out outer)) {Hide();return;}
        int x=origin.X+(right?client.Right-(outer.Right-outer.Left)-12:12), y=origin.Y+12;
        if(x!=outer.Left || y!=outer.Top || Opacity==0)
            FpsWindows.SetWindowPos(handle,new IntPtr(-1),x,y,0,0,0x11);
        Opacity=1;
    }
}

