using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;

// One rolling window per swap chain. Use the timestamps of the presents, since
// pipe delivery can be batched. Two swap chains must never double the readout.
internal sealed class FrameSamples {
    sealed class Chain {
        internal readonly Queue<double> Times=new Queue<double>();
        internal long Updated;
        internal double LastTime;
    }
    readonly Dictionary<string,Chain> chains=new Dictionary<string,Chain>();
    readonly string pid;
    int process=-1,address=-1,time=-1;
    internal FrameSamples(int pid) {this.pid=pid.ToString(CultureInfo.InvariantCulture);}
    static int FromEnd(string[] header,string column) {
        int index=Array.IndexOf(header,column);
        return index<1?-1:header.Length-1-index;
    }
    internal void Read(string line,long now) {
        if(String.IsNullOrEmpty(line)) return;
        if(line.StartsWith("Application,",StringComparison.OrdinalIgnoreCase)) {
            var header=line.Split(',');
            process=FromEnd(header,"ProcessID"); address=FromEnd(header,"SwapChainAddress");
            time=FromEnd(header,"TimeInSeconds"); return;
        }
        if(process<0 || address<0 || time<0) return;
        // Read only the three fields we need. Counting from the right also
        // handles application filenames containing commas.
        int end=line.Length; string key=null; double seconds=-1;
        int last=Math.Max(process,Math.Max(address,time));
        for(int i=0;i<=last;i++) {
            if(end==0) return;
            int comma=line.LastIndexOf(',',end-1);
            if(comma<0) return;
            int begin=comma+1,count=end-begin;
            if(i==process && (count!=pid.Length || String.CompareOrdinal(line,begin,pid,0,count)!=0)) return;
            if(i==address) key=line.Substring(begin,count);
            if(i==time && !Double.TryParse(line.Substring(begin,count),NumberStyles.Float,CultureInfo.InvariantCulture,out seconds)) return;
            end=comma;
        }
        if(String.IsNullOrEmpty(key) || seconds<0 || Double.IsInfinity(seconds) || Double.IsNaN(seconds)) return;
        lock(chains) {
            Chain chain;
            if(!chains.TryGetValue(key,out chain)) {
                if(chains.Count>=8) chains.Remove(chains.OrderBy(pair=>pair.Value.Updated).First().Key);
                chains[key]=chain=new Chain();
            }
            var times=chain.Times;
            if(times.Count!=0 && seconds<chain.LastTime) times.Clear();
            if(times.Count!=0 && seconds==chain.LastTime) return;
            while(times.Count>=2048 || times.Count>1 && seconds-times.Peek()>1.0) times.Dequeue();
            times.Enqueue(seconds); chain.LastTime=seconds; chain.Updated=now;
        }
    }
    internal double Rate(long now) {
        lock(chains) {
            Chain best=null;
            foreach(var chain in chains.Values) {
                if(now-chain.Updated>2*Stopwatch.Frequency || chain.Times.Count<2) continue;
                if(best==null || chain.Times.Count>best.Times.Count ||
                    chain.Times.Count==best.Times.Count && chain.Updated>best.Updated) best=chain;
            }
            return best==null ? 0 : (best.Times.Count-1)/(best.LastTime-best.Times.Peek());
        }
    }
}

internal static class FpsCapture {
    const string Version="2.4.1";
    const int MaxBytes=16*1024*1024;
    internal static string GetTool(CancellationToken cancel) {
        bool x64=Environment.Is64BitOperatingSystem;
        string hash=x64?"d74183e7ae630f72cd3690be0373ecbfdc6cbb86578148aab8fa2a7166068f34":"360a7a6ba18dc95c60874fc78dc31a7c4103b48030d8d41c8569792a4b787c66";
        string name="PresentMon-"+Version+(x64?"-x64.exe":"-x86.exe");
        string dir=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"GameDock","Tools",Version);
        string path=Path.Combine(dir,name);
        if(Verified(path,hash)) return path;
        Directory.CreateDirectory(dir);
        string temp=path+"."+Guid.NewGuid().ToString("N")+".tmp";
        try {
            var request=(HttpWebRequest)WebRequest.Create("https://github.com/GameTechDev/PresentMon/releases/download/v"+Version+"/"+name);
            request.UserAgent="GameDock"; request.Timeout=15000; request.ReadWriteTimeout=15000;
            ServicePointManager.SecurityProtocol|=SecurityProtocolType.Tls12;
            using(cancel.Register(request.Abort))
            using(var response=request.GetResponse())
            using(var input=response.GetResponseStream())
            using(var output=new FileStream(temp,FileMode.CreateNew,FileAccess.Write,FileShare.None)) {
                byte[] buf=new byte[32768]; int n;
                while((n=input.Read(buf,0,buf.Length))!=0) {
                    cancel.ThrowIfCancellationRequested();
                    if(output.Length+n>MaxBytes) throw new InvalidDataException();
                    output.Write(buf,0,n);
                }
            }
            cancel.ThrowIfCancellationRequested();
            if(!Verified(temp,hash)) throw new InvalidDataException("PresentMon integrity check failed");
            if(File.Exists(path)) File.Replace(temp,path,null); else File.Move(temp,path);
            return path;
        } finally {if(File.Exists(temp)) File.Delete(temp);}
    }
    static bool Verified(string file,string hash) {
        if(!File.Exists(file) || new FileInfo(file).Length>MaxBytes) return false;
        using(var stream=File.OpenRead(file))
        using(var sha=SHA256.Create())
            return String.Equals(BitConverter.ToString(sha.ComputeHash(stream)).Replace("-",""),hash,StringComparison.OrdinalIgnoreCase);
    }
    static ProcessStartInfo StartInfo(string file,string args) {
        return new ProcessStartInfo(file,args) {UseShellExecute=false,CreateNoWindow=true,WorkingDirectory=Path.GetDirectoryName(file)};
    }
    internal static void Run(string tool,int pid,FrameSamples samples,CancellationToken token) {
        string session="GameDockFPS-"+Guid.NewGuid().ToString("N");
        var start=StartInfo(tool,"--session_name "+session+" --process_id "+pid+" --output_stdout --no_console_stats --v1_metrics --no_track_gpu --no_track_display --no_track_input --terminate_on_proc_exit");
        start.RedirectStandardOutput=true; start.RedirectStandardError=true;
        using(var p=new Process {StartInfo=start}) {
            string error=""; bool denied=false;
            p.OutputDataReceived+=(s,e)=>samples.Read(e.Data,Stopwatch.GetTimestamp());
            p.ErrorDataReceived+=(s,e)=>{
                if(String.IsNullOrEmpty(e.Data)) return;
                if(e.Data.IndexOf("access denied",StringComparison.OrdinalIgnoreCase)>=0) denied=true;
                error=e.Data;
            };
            token.ThrowIfCancellationRequested();
            p.Start();
            try {
                p.BeginOutputReadLine(); p.BeginErrorReadLine();
                while(!p.WaitForExit(200)) token.ThrowIfCancellationRequested();
                p.WaitForExit();
                if(p.ExitCode!=0) {
                    if(denied) throw new UnauthorizedAccessException(error);
                    throw new InvalidOperationException(error);
                }
            } finally {
                if(!p.HasExited) {
                    using(var stop=Process.Start(StartInfo(tool,"--session_name "+session+" --terminate_existing_session"))) {
                        if(!stop.WaitForExit(1200)) stop.Kill();
                    }
                    if(!p.WaitForExit(1200)) p.Kill();
                }
            }
        }
    }
}

internal static class FpsWindows {
    internal delegate void FocusEvent(IntPtr hook,uint ev,IntPtr window,int obj,int child,uint thread,uint time);
    [DllImport("user32")] internal static extern IntPtr SetWinEventHook(uint first,uint last,IntPtr module,FocusEvent callback,uint pid,uint thread,uint flags);
    [DllImport("user32")] internal static extern bool UnhookWinEvent(IntPtr hook);
    [DllImport("user32")] internal static extern IntPtr GetForegroundWindow();
    [DllImport("user32")] internal static extern uint GetWindowThreadProcessId(IntPtr hwnd,out int pid);
    [DllImport("user32",CharSet=CharSet.Unicode)] internal static extern int GetWindowText(IntPtr hwnd,StringBuilder buffer,int size);
    [DllImport("kernel32")] static extern IntPtr OpenProcess(uint access,bool inherit,int pid);
    [DllImport("kernel32",CharSet=CharSet.Unicode)] static extern bool QueryFullProcessImageName(IntPtr process,uint flags,StringBuilder path,ref int size);
    [DllImport("kernel32")] static extern bool CloseHandle(IntPtr handle);
    internal static string ImagePath(int pid) {
        var h=OpenProcess(0x1000,false,pid);
        if(h==IntPtr.Zero) return null;
        try {
            int length=32768; var path=new StringBuilder(length);
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

internal sealed class FpsHud : Window {
    readonly TextBlock number=new TextBlock {FontSize=17,FontWeight=FontWeights.SemiBold,Foreground=Brushes.White,VerticalAlignment=VerticalAlignment.Center};
    internal FpsHud() {
        Title="GameDock FPS"; WindowStyle=WindowStyle.None; AllowsTransparency=true;
        Background=Brushes.Transparent; ResizeMode=ResizeMode.NoResize; ShowInTaskbar=false;
        ShowActivated=false; Topmost=true; Focusable=false; IsHitTestVisible=false;
        Width=88; Height=32; WindowStartupLocation=WindowStartupLocation.Manual;
        var row=new StackPanel {Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Center};
        row.Children.Add(number);
        row.Children.Add(new TextBlock {Text="FPS",FontSize=10,FontWeight=FontWeights.Bold,Foreground=Brushes.PaleTurquoise,
            VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(5,1,0,0)});
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
        number.Text=rate<1?rate.ToString("0.0",CultureInfo.InvariantCulture):Math.Round(rate).ToString(CultureInfo.InvariantCulture);
        if(!IsVisible) {Opacity=0;Show();}
        var handle=new WindowInteropHelper(this).Handle;
        if(!FpsWindows.GetWindowRect(handle,out outer)) {Hide();return;}
        int x=origin.X+(right?client.Right-(outer.Right-outer.Left)-12:12), y=origin.Y+12;
        if(x!=outer.Left || y!=outer.Top || Opacity==0)
            FpsWindows.SetWindowPos(handle,new IntPtr(-1),x,y,0,0,0x11);
        Opacity=1;
    }
}

partial class GameDock {
    bool fpsEnabled=Preference("FpsEnabled",0,0,1)==1;
    bool fpsAccessDenied;
    int fpsCorner=Preference("FpsCorner",0,0,1);
    string[] fpsExtra=ReadSetting(prefs,"FpsGames",null) as string[] ?? new string[0];
    FpsWindows.FocusEvent fpsCallback;
    IntPtr fpsHook,fpsWindow;
    int fpsPid;
    string fpsFailure;
    FpsHud fpsHud;
    DispatcherTimer fpsTimer;
    FrameSamples fpsSamples;
    CancellationTokenSource fpsStop;
    Task fpsWorker=Task.FromResult(0);
    void StartFps() {
        if(fpsCallback!=null) return;
        fpsCallback=delegate {if(!closing && fpsEnabled) FocusFps();};
        fpsHook=FpsWindows.SetWinEventHook(3,3,IntPtr.Zero,fpsCallback,0,0,0);
        if(fpsHook==IntPtr.Zero) {fpsFailure=T("تعذّر تتبّع نافذة اللعبة.","Windows could not track the foreground game.");return;}
        fpsTimer=new DispatcherTimer {Interval=TimeSpan.FromMilliseconds(500)};
        fpsTimer.Tick+=PaintFps;
        FocusFps();
    }
    static bool MatchesFpsTarget(string path,string proof) {
        return String.Equals(path,proof,StringComparison.OrdinalIgnoreCase);
    }
    bool FpsGame(string path,IntPtr window) {
        if(String.IsNullOrEmpty(path) || GameScan.IsLauncher(path)) return false;
        if(fpsExtra.Contains(path,Paths)) return true;
        if(automatic && detected.Games.Values.Any(g=>MatchesFpsTarget(path,g.Proof) || MatchesFpsTarget(path,g.Target))) return true;
        if(!automatic && manualGames.Values.Any(g=>!g.LauncherOnly && MatchesFpsTarget(path,g.Target))) return true;
        string file=Path.GetFileNameWithoutExtension(path);
        if(!Paths.Equals(file,"javaw") && !Paths.Equals(file,"java")) return false;
        var title=new StringBuilder(256); FpsWindows.GetWindowText(window,title,title.Capacity);
        return System.Text.RegularExpressions.Regex.IsMatch(title.ToString(),@"^Minecraft(?:$|[\s*:-])",System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }
    void FocusFps() {
        if(fpsTimer==null || !fpsEnabled || closing || fpsAccessDenied) return;
        IntPtr window=FpsWindows.GetForegroundWindow(); int pid=0;
        if(window!=IntPtr.Zero) FpsWindows.GetWindowThreadProcessId(window,out pid);
        if(pid!=0 && pid==fpsPid) {fpsWindow=window;return;}
        if(pid!=0 && !FpsGame(FpsWindows.ImagePath(pid),window)) pid=0;
        if(pid==fpsPid) return;
        EndFpsCapture(); if(pid==0) return;
        fpsPid=pid; fpsWindow=window;
        var stop=fpsStop=new CancellationTokenSource(); var token=stop.Token;
        var frames=fpsSamples=new FrameSamples(pid);
        fpsTimer.Start();
        fpsWorker=fpsWorker.ContinueWith(delegate {
            try {
                if(token.WaitHandle.WaitOne(250)) return;
                string tool;
                try {tool=FpsCapture.GetTool(token);}
                catch(Exception) {ReportFps(stop,true,false);return;}
                FpsCapture.Run(tool,pid,frames,token);
            } catch(OperationCanceledException) {}
            catch(UnauthorizedAccessException) {ReportFps(stop,false,true);}
            catch(Exception) {ReportFps(stop,false,false);}
            finally {Dispatcher.BeginInvoke(new Action(delegate {
                if(fpsStop==stop) {fpsTimer.Stop(); if(fpsHud!=null) fpsHud.Hide();}
                stop.Dispose();
            }));}
        },TaskScheduler.Default);
    }
    void ReportFps(CancellationTokenSource stop,bool download,bool denied) {
        if(stop.IsCancellationRequested) return;
        Dispatcher.BeginInvoke(new Action(delegate {
            if(closing || !fpsEnabled || fpsStop!=stop) return;
            fpsAccessDenied=denied;
            fpsFailure=download?T("تعذّر تنزيل أداة القياس أو التحقق منها. افحص الاتصال ثم ارجع للعبة.","Could not download or verify the measurement tool. Check your connection and return to the game."):
                denied?T("رفض ويندوز صلاحية القياس. يمكن لمسؤول الجهاز إضافة حسابك إلى Performance Log Users. بعد تسجيل الخروج والدخول، افتح الدوك مجددًا.","Windows denied tracing access. A device administrator can add your account to Performance Log Users. Sign out and back in, then reopen GameDock."):
                T("توقف القياس لهذه اللعبة. ارجع إلى اللعبة لإعادة المحاولة.","Capture stopped for this game. Return to the game to try again.");
            if(settingsWindow!=null && settingsPage==2 && !minecraftDetails) DrawSettings();
        }));
    }
    void PaintFps(object sender,EventArgs e) {
        if(fpsWindow!=FpsWindows.GetForegroundWindow()) {FocusFps();return;}
        double rate=fpsSamples.Rate(Stopwatch.GetTimestamp());
        if(rate<=0) {if(fpsHud!=null && fpsHud.IsVisible) fpsHud.Hide();return;}
        fpsFailure=null;
        if(fpsHud==null) fpsHud=new FpsHud();
        fpsHud.Display(fpsWindow,rate,fpsCorner==1);
    }
    void EndFpsCapture() {
        if(fpsTimer!=null) fpsTimer.Stop();
        if(fpsHud!=null && fpsHud.IsVisible) fpsHud.Hide();
        if(fpsStop!=null) {try {fpsStop.Cancel();} catch(ObjectDisposedException) {} fpsStop=null;}
        fpsPid=0; fpsWindow=IntPtr.Zero; fpsSamples=null;
    }
    void StopFps() {
        if(fpsHook!=IntPtr.Zero) FpsWindows.UnhookWinEvent(fpsHook);
        fpsHook=IntPtr.Zero; fpsCallback=null; EndFpsCapture();
        fpsTimer=null;
        if(fpsHud!=null) {fpsHud.Close();fpsHud=null;}
        if(closing && !fpsWorker.IsCompleted) {try {fpsWorker.Wait(3500);} catch(AggregateException) {}}
    }
    void SaveFpsGames(string[] paths) {
        try {
            Registry.SetValue(prefs,"FpsGames",paths,RegistryValueKind.MultiString);
            fpsExtra=paths; DrawSettings();
        } catch(Exception e) {Alert(e.Message);}
    }
    void DrawFpsSettings(StackPanel body) {
        AddSwitch(body,T("عرض عدّاد FPS أثناء اللعب","Show FPS while playing"),fpsEnabled,delegate(bool on){
            fpsEnabled=on; SavePreference("FpsEnabled",on?1:0); fpsFailure=null; fpsAccessDenied=false;
            if(on) StartFps(); else StopFps();
            DrawSettings();
        });
        if(!fpsEnabled) return;
        Detail(body,fpsFailure ?? T("يظهر فوق نافذة اللعبة ويختفي عند تبديل التطبيق. عند أول استخدام يُنزّل GameDock أداة PresentMon من Intel. استخدم وضع النافذة أو ملء الشاشة بلا حدود.",
            "Shows presented FPS while your game is focused. GameDock downloads Intel PresentMon on first use. Use windowed or borderless fullscreen."));
        Section(body,T("مكان العدّاد","Counter position"),new[]{T("أعلى اليسار","Top left"),T("أعلى اليمين","Top right")},fpsCorner,
            delegate(int i){fpsCorner=i;SavePreference("FpsCorner",i);});
        body.Children.Add(SettingButton(T("إضافة ملف لعبة للقياس…","Add a game executable…"),delegate {
            var pick=new OpenFileDialog {Filter="Game executable (*.exe)|*.exe",Multiselect=true};
            if(pick.ShowDialog(settingsWindow)==true) SaveFpsGames(fpsExtra.Concat(pick.FileNames).Distinct(Paths).ToArray());
        },false));
        foreach(string path in fpsExtra) {
            string item=path; var remove=SettingButton(T("إزالة ","Remove ")+Path.GetFileName(item),delegate {SaveFpsGames(fpsExtra.Where(p=>!Paths.Equals(p,item)).ToArray());},false);
            remove.ToolTip=item; body.Children.Add(remove);
        }
    }
}
