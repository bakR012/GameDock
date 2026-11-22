using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

[assembly: System.Reflection.AssemblyTitle("GameDock")]
[assembly: System.Reflection.AssemblyProduct("GameDock")]
[assembly: System.Reflection.AssemblyFileVersion("1.7.2.0")]
class GameDock : Window {
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    static extern int GetWindowLong(IntPtr window, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    static extern int SetWindowLong(IntPtr window, int index, int value);

        [ComImport, Guid("56FDF342-FD6D-11d0-958A-006097C9A090"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface ITaskbarList {
        void HrInit(); void AddTab(IntPtr hwnd); void DeleteTab(IntPtr hwnd);
    }
    bool showInSwitcher = Preference("ShowInSwitcher",0,0,1)==1;
    static void SetSwitcher(Window window,bool visible) {
        const int extendedStyle = -20, toolWindow = 0x80, appWindow = 0x40000;
        var hwnd = new WindowInteropHelper(window).Handle;
        int style = GetWindowLong(hwnd, extendedStyle);
                SetWindowLong(hwnd, extendedStyle, visible ? (style | appWindow) & ~toolWindow : (style | toolWindow) & ~appWindow);
        if (visible) window.Dispatcher.BeginInvoke(new Action(delegate {
            if (!window.IsVisible) return;
            ITaskbarList list=null;
            try {
                list=(ITaskbarList)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("56FDF344-FD6D-11d0-958A-006097C9A090")));
                list.HrInit(); list.DeleteTab(hwnd);
            } catch(COMException) {} finally {if(list!=null) Marshal.ReleaseComObject(list);}
        }),DispatcherPriority.ApplicationIdle);
    }
    readonly GameDock host;
    readonly List<GameDock> mirrors = new List<GameDock>();
    Forms.Screen screen;
    GameDock settingsAnchor;
    string selectedDisplay = ReadSetting(prefs, "Display", "primary") as string ?? "primary";

    Rect WorkArea {
        get {
            var bounds = screen.WorkingArea;
            var primary = Forms.Screen.PrimaryScreen.Bounds;
            double scale = SystemParameters.PrimaryScreenWidth / primary.Width;
            return new Rect(bounds.Left * scale, bounds.Top * scale, bounds.Width * scale, bounds.Height * scale);
        }
    }
    void AddDisplaySetting(StackPanel body) {
        var screens = Forms.Screen.AllScreens.OrderBy(s => s.DeviceName, Paths).ToArray();
        var choices = new List<string> { T("الشاشة الرئيسية", "Primary display"), T("كل الشاشات", "All displays") };
        var keys = new List<string> { "primary", "all" };
        foreach (var display in screens) {
            keys.Add(display.DeviceName);
            choices.Add(T("شاشة ", "Display ") + display.DeviceName.Substring(display.DeviceName.LastIndexOf('Y') + 1)
                + " (" + display.Bounds.Width + " × " + display.Bounds.Height + ")");
        }
        if (!keys.Contains(selectedDisplay)) {
            keys.Add(selectedDisplay);
            choices.Add(T("الشاشة المحفوظة غير متصلة", "Saved display disconnected"));
        }
        Section(body, T("عرض الشريط على", "Show dock on"), choices.ToArray(), keys.IndexOf(selectedDisplay), delegate(int i) {
            try { Registry.SetValue(prefs, "Display", keys[i]); }
            catch (Exception e) { Alert(e.Message); return; }
            selectedDisplay = keys[i];
            ArrangeDisplays();
        });
    }
    void DisplayChanged(object sender, EventArgs e) {
        Dispatcher.BeginInvoke(new Action(delegate {
            if (closing) return;
            ArrangeDisplays();
            if (settingsWindow != null) DrawSettings();
        }));
    }
    void ArrangeDisplays() {
        ArrangeDisplays(Forms.Screen.AllScreens, Forms.Screen.PrimaryScreen);
    }
    void ArrangeDisplays(Forms.Screen[] screens, Forms.Screen primary) {
        screen = screens.FirstOrDefault(s => Paths.Equals(s.DeviceName, selectedDisplay)) ?? primary;
        var extra = screens.Where(s => selectedDisplay == "all" && !Paths.Equals(s.DeviceName, screen.DeviceName)).ToArray();
        foreach (var dock in mirrors.ToArray()) {
            var display = extra.FirstOrDefault(s => Paths.Equals(s.DeviceName, dock.screen.DeviceName));
            if (display != null) { dock.screen = display; dock.Fit(); dock.Position(); continue; }
            if (settingsAnchor == dock) settingsAnchor = this;
            mirrors.Remove(dock); dock.Close();
        }
        foreach (var display in extra) {
            if (mirrors.Any(d => Paths.Equals(d.screen.DeviceName, display.DeviceName))) continue;
            var dock = new GameDock(this, display);
            mirrors.Add(dock);
            dock.folder = folder;
            dock.Show();
            dock.RenderEntries(entries, false);
            dock.Position();
        }
        Fit(); Position();
    }
    void SyncMirrors(string setting) {
        foreach (var dock in mirrors) {
            dock.useTray = useTray; dock.english = english; dock.edge = edge;
            dock.theme = theme; dock.gradient = gradient; dock.visibleCount = visibleCount;
            for (int i = 0; i < colors.Length; i++) {
                dock.colors[i].Hue = colors[i].Hue; dock.colors[i].Saturation = colors[i].Saturation;
                dock.colors[i].Brightness = colors[i].Brightness;
            }
            if (setting == "DockEdge" || setting == "English" || setting == "TrayAccess") {
                dock.SetEdgeLayout(); dock.RenderEntries(entries, false); dock.Position();
            } else if (setting == "VisibleCount") dock.Fit();
            else dock.ApplyTheme();
        }
    }
    readonly Canvas icons = new Canvas();
    string[] entries = new string[0];
    readonly Dictionary<int,Button> visibleTiles = new Dictionary<int,Button>();
    int firstTile = -1, lastTile = -1;
    readonly ScrollViewer strip = new ScrollViewer();
    readonly DispatcherTimer timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(240) };
    readonly string settings = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GameDock", "folder.txt");
    string folder = "";
    FileSystemWatcher watcher;
    int folderRefreshPending;
    bool closing;
    Forms.NotifyIcon tray;
    readonly StackPanel access = new StackPanel { Orientation = Orientation.Horizontal };
    System.Drawing.Icon logo;
    Window settingsWindow;
    bool english, useTray;
    string T(string ar, string en) { return english ? en : ar; }
    const string runKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const int Slot = 52;
    const string prefs = @"HKEY_CURRENT_USER\Software\GameDock";
    readonly Border shell = new Border();
    readonly Stopwatch motion = new Stopwatch();
    double targetOffset, scrollPosition;
    int visibleCount = 12;
    int theme;
    bool gradient = Preference("Gradient",1,0,1) == 1;
    int activeColor;
    sealed class DockColor {
        public readonly string Key;
        public int Hue, Saturation, Brightness;
        public DockColor(string key,int hue,int saturation,int brightness,int neutralBrightness) {
            Key = key;
            bool customized = new[] { "Hue", "Saturation", "Brightness" }.Any(
                part => ReadSetting(prefs,key+part,null) != null);
            if (!customized) { hue = 0; saturation = 0; brightness = neutralBrightness; }
            Hue = Preference(key+"Hue",hue,0,360);
            Saturation = Preference(key+"Saturation",saturation,0,100);
            Brightness = Preference(key+"Brightness",brightness,0,100);
        }
        public System.Windows.Media.Color Rgb(byte alpha) {
            double h = (Hue % 360)/60.0, v = Brightness/100.0;
            double c = v*Saturation/100.0, x = c*(1-Math.Abs(h%2-1)), m = v-c;
            double r=0,g=0,b=0;
            switch ((int)h) {
                case 0: r=c; g=x; break;
                case 1: r=x; g=c; break;
                case 2: g=c; b=x; break;
                case 3: g=x; b=c; break;
                case 4: r=x; b=c; break;
                default: r=c; b=x; break;
            }
            return System.Windows.Media.Color.FromArgb(alpha,(byte)Math.Round((r+m)*255),
                (byte)Math.Round((g+m)*255),(byte)Math.Round((b+m)*255));
        }
    }
    readonly DockColor[] colors = { new DockColor("Color1",253,47,35,92),new DockColor("Color2",192,75,38,52) };
    Brush panelPaint, outlinePaint, labelPaint;
    static object ReadSetting(string key, string name, object fallback) {
        try { return Registry.GetValue(key, name, fallback); }
        catch (System.Security.SecurityException) { return fallback; }
        catch (UnauthorizedAccessException) { return fallback; }
    }
    static int Preference(string name, int fallback, int min, int max) {
        object value = ReadSetting(prefs, name, fallback);
        return value is int ? Math.Max(min, Math.Min(max, (int)value)) : fallback;
    }
    void SavePreference(string name, int value) {
        try { Registry.SetValue(prefs, name, value, RegistryValueKind.DWord); if (host == this) SyncMirrors(name); }
        catch (Exception e) { Alert(T("تعذّر حفظ الإعداد.", "Could not save this setting.") + "\n" + e.Message); }
    }
    void ApplyTheme() {
        if (theme == 2) {
            var first = colors[0].Rgb(224);
            panelPaint = gradient ? (Brush)new LinearGradientBrush(first,colors[1].Rgb(224),Side ? 90 : 0)
                : new SolidColorBrush(first);
        } else panelPaint = Color(theme == 0 ? "#73212121" : "#F0141414");
        outlinePaint = Color(theme == 2 ? "#80EEEEEE" : theme == 1 ? "#64666666" : "#80DDDDDD");
        labelPaint = Brushes.White;
        shell.Background = panelPaint;
        shell.BorderBrush = outlinePaint;
    }
    static System.Windows.Media.Color CaptionColor(System.Windows.Media.Color c) {
        return System.Windows.Media.Color.FromArgb(248,(byte)(c.R/3),(byte)(c.G/3),(byte)(c.B/3));
    }
    readonly StackPanel row = new StackPanel();
    Dock edge = (Dock)Preference("DockEdge",(int)Dock.Bottom,0,3);
    bool Side { get { return edge == Dock.Left || edge == Dock.Right; } }
    const int TileHeight = 49;
    int Step { get { return Side ? TileHeight : Slot; } }
    double ScrollOffset { get { return Side ? strip.VerticalOffset : strip.HorizontalOffset; } }
    double ScrollLimit { get { return Side ? strip.ScrollableHeight : strip.ScrollableWidth; } }
    double Along(Point pt) { return Side ? pt.Y : pt.X; }
    void ScrollTo(double offset) {
        if (Side) strip.ScrollToVerticalOffset(offset); else strip.ScrollToHorizontalOffset(offset);
    }
    void SetEdgeLayout() {
        StopScroll(); ClearMarker();
        row.Orientation = access.Orientation = Side ? Orientation.Vertical : Orientation.Horizontal;
        strip.VerticalScrollBarVisibility = Side ? ScrollBarVisibility.Hidden : ScrollBarVisibility.Disabled;
        strip.HorizontalScrollBarVisibility = Side ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Hidden;
        ApplyAccess(); ApplyTheme();
    }
    void StartScroll(int delta) {
        if (!motion.IsRunning) targetOffset = scrollPosition = ScrollOffset;
        targetOffset = Math.Max(0, Math.Min(ScrollLimit, targetOffset - Step * delta / 120.0));
        if (!motion.IsRunning && targetOffset != scrollPosition) {
            motion.Start();
            CompositionTarget.Rendering += ScrollFrame;
        }
    }
    void StopScroll() {
        CompositionTarget.Rendering -= ScrollFrame;
        motion.Reset();
    }
    void ScrollFrame(object sender, EventArgs e) {
        double elapsed = motion.Elapsed.TotalSeconds;
        motion.Restart();
        targetOffset = Math.Min(ScrollLimit, targetOffset);
        scrollPosition += (targetOffset - scrollPosition) * (1 - Math.Exp(-24 * elapsed));
        if (Math.Abs(targetOffset - scrollPosition) < 0.4) {
            scrollPosition = targetOffset;
            StopScroll();
        }
        ScrollTo(scrollPosition);
    }

    [StructLayout(LayoutKind.Sequential, CharSet=CharSet.Unicode)]
    struct FileIcon {
        public IntPtr handle;
        public int index;
        public uint attributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst=260)] public string name;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst=80)] public string type;
    }
    [DllImport("shell32.dll", CharSet=CharSet.Unicode)]
    static extern IntPtr SHGetFileInfo(string path, uint attributes, out FileIcon info, uint size, uint flags);
    [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr handle);

    [DllImport("comctl32.dll")]
    static extern IntPtr ImageList_GetIcon(IntPtr list, int index, uint flags);

    static Brush Color(string hex) {
        var brush = (SolidColorBrush)new BrushConverter().ConvertFromString(hex);
        brush.Freeze();
        return brush;
    }
    static void Alert(string message) { MessageBox.Show(message, "GameDock"); }
    void Launch(string path,string arguments = "") {
        try { Process.Start(new ProcessStartInfo(path,arguments ?? "") { UseShellExecute = true, WorkingDirectory = File.Exists(path) ? Path.GetDirectoryName(path) : "" }); }
        catch (Exception e) { Alert(T("تعذّر فتح العنصر.", "Could not open this item.") + "\n" + e.Message); }
    }
    const string OrderKey = prefs + @"\Order";
    static readonly StringComparer Paths = StringComparer.OrdinalIgnoreCase;
    static string DesktopFolder { get { return Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory); } }
    string DockStorage {
        get { return SameFolder(folder,DesktopFolder)
            ? Path.Combine(Path.GetDirectoryName(settings),"DesktopDock") : folder; }
    }
    string InFolder(string name) {
        string stored = Path.Combine(DockStorage,name);
        return File.Exists(stored) ? stored : Path.Combine(folder,name);
    }
    string OrderName { get { return host.automatic ? "DetectedGames" : Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar); } }
    const string HiddenKey = prefs + @"\Hidden";
    string[] HiddenNames() { return ReadSetting(HiddenKey,OrderName,null) as string[] ?? new string[0]; }
    bool automatic = Preference("AutomaticGames",1,0,1) == 1;
    bool downloadIcons=Preference("AutoDownloadIcons",1,0,1)==1;
    bool lastMinecraftProfile=Preference("LastMinecraftProfile",0,0,1)==1;
    GameScan detected = new GameScan();
    readonly Dictionary<string,FileSystemWatcher> gameWatchers = new Dictionary<string,FileSystemWatcher>(StringComparer.OrdinalIgnoreCase);
    bool scanning, rescan;
    int retryCount;
    void ScanGames() {
        if (host != this) { host.ScanGames(); return; }
        if (!automatic || closing) return;
        if (scanning) { rescan = true; return; }
        scanning = true;
        System.Threading.Tasks.Task.Factory.StartNew<GameScan>(GameScan.Read).ContinueWith(task => Dispatcher.BeginInvoke(new Action(delegate {
            scanning = false;
            if (closing || !automatic) return;
            if (!task.IsFaulted) {
                var result = task.Result;
                bool addedWatcher = false;
                foreach (var key in gameWatchers.Keys.Except(result.Watches,StringComparer.OrdinalIgnoreCase).ToArray()) {
                    gameWatchers[key].Dispose(); gameWatchers.Remove(key);
                }
                foreach (var key in result.Watches.Where(k => !gameWatchers.ContainsKey(k))) {
                    var parts = key.Split('|');
                    FileSystemWatcher watch = null;
                    try {
                        watch = new FileSystemWatcher(parts[0],parts[1]);
                        watch.NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName |
                            (parts[1] == "*" ? 0 : NotifyFilters.LastWrite);
                        watch.Changed += Changed; watch.Created += Changed; watch.Deleted += Changed; watch.Renamed += Changed;
                        watch.Error += Changed; watch.EnableRaisingEvents = true;
                        gameWatchers.Add(key,watch); addedWatcher = true;
                    } catch (IOException) { if (watch != null) watch.Dispose(); }
                      catch (UnauthorizedAccessException) { if (watch != null) watch.Dispose(); }
                      catch (ArgumentException) { if (watch != null) watch.Dispose(); }
                }
                // Keep the current dock during a short-lived manifest rewrite.
                if (!result.Retry || retryCount>=2 || detected.Games.Count==0) {
                    var previous=detected;
                    detected=result;
                    var list=Entries();
                    if(!entries.SequenceEqual(list,Paths) || list.Any(id=>{
                        InstalledGame before;
                        var after=result.Games[id];
                        return !previous.Games.TryGetValue(id,out before) || before.Title!=after.Title ||
                            before.Icon!=after.Icon || before.ShortcutIcon!=after.ShortcutIcon || before.Target!=after.Target || before.Arguments!=after.Arguments ||
                            before.Proof!=after.Proof || before.Manifest!=after.Manifest;
                    })) Refresh();
                }
                if (result.Retry && retryCount++ < 2) Changed(this,EventArgs.Empty);
                if (!result.Retry) retryCount = 0;
                rescan |= addedWatcher;
            }
            if (rescan) { rescan = false; ScanGames(); }
        })));
    }
    void StopGameWatchers() {
        foreach (var watcher in gameWatchers.Values) watcher.Dispose();
        gameWatchers.Clear();
    }
    readonly Dictionary<string,ManualGame> manualGames=new Dictionary<string,ManualGame>(Paths);
    string[] Entries() {
        manualGames.Clear();
        if (host.automatic) {
            var ids = host.detected.Games.OrderBy(pair=>pair.Value.Title,StringComparer.CurrentCultureIgnoreCase).Select(pair=>pair.Key).ToArray();
            var order = ReadSetting(OrderKey,OrderName,null) as string[] ?? new string[0];
            var hidden=new HashSet<string>(HiddenNames().Select(host.detected.Resolve),Paths);
            var ranked=order.Select(host.detected.Resolve).Intersect(ids,Paths);
            return ranked.Concat(ids.Except(ranked,Paths)).Where(id=>!hidden.Contains(id) &&
                !hidden.Contains("title:"+GameArtwork.Normalize(host.detected.Games[id].Title))).ToArray();
        }
        var items = new DirectoryInfo(folder).GetFileSystemInfos().AsEnumerable();
        if (!SameFolder(folder,DockStorage) && Directory.Exists(DockStorage))
            items = items.Concat(new DirectoryInfo(DockStorage).GetFileSystemInfos());
        var entries = items
            .Where(f => (f.Attributes & (FileAttributes.Hidden | FileAttributes.System)) == 0)
            .Select(f => f.Name).OrderBy(n => n,StringComparer.CurrentCultureIgnoreCase).ToArray();
        object shortcutShell=null;
        try {
            foreach(var name in entries.Distinct(Paths)) {
                string path=InFolder(name);
                if(!Link(path) && !Url(path) && !Paths.Equals(Path.GetExtension(path),".exe")) continue;
                if(Link(path) && shortcutShell==null) shortcutShell=Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell"));
                manualGames[name]=ManualGame.Read(path,shortcutShell);
            }
        } finally {if(shortcutShell!=null) Marshal.FinalReleaseComObject(shortcutShell);}
        entries=entries.Where(name=>!manualGames.ContainsKey(name) || !manualGames[name].LauncherOnly).ToArray();
        var saved = ReadSetting(OrderKey,OrderName,null) as string[] ?? new string[0];
        return saved.Intersect(entries,Paths).Concat(entries.Except(saved,Paths)).Except(HiddenNames(),Paths).ToArray();
    }
    static bool Link(string path) { return Paths.Equals(Path.GetExtension(path),".lnk"); }
    static bool Url(string path) { return Paths.Equals(Path.GetExtension(path),".url"); }
    static void WithLink(string path, Action<dynamic> use) {
        dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell",true));
        try {
            dynamic link = shell.CreateShortcut(path);
            try { use(link); } finally { Marshal.FinalReleaseComObject(link); }
        } finally { Marshal.FinalReleaseComObject(shell); }
    }
    void Repair(string path) {
        try {
            var pick = new OpenFileDialog { Title = T("اختر الموقع الجديد","Locate the new target"), DereferenceLinks = false };
            if (Url(path)) pick.Filter = T("اختصار إنترنت","Internet shortcut") + "|*.url";
            if (pick.ShowDialog(this) != true || Paths.Equals(path,pick.FileName)) return;
            if (Url(path) || Link(pick.FileName)) {
                string temp = Path.Combine(Path.GetDirectoryName(path),Guid.NewGuid().ToString("N") + Path.GetExtension(path));
                try { File.Copy(pick.FileName,temp); File.Replace(temp,path,null); }
                finally { if (File.Exists(temp)) File.Delete(temp); }
            } else WithLink(path, delegate(dynamic link) {
                string previous = link.TargetPath;
                if (String.IsNullOrEmpty((string)link.WorkingDirectory) ||
                    Paths.Equals((string)link.WorkingDirectory,Path.GetDirectoryName(previous)))
                    link.WorkingDirectory = Path.GetDirectoryName(pick.FileName);
                link.TargetPath = pick.FileName;
                link.Save();
            });
            Refresh();
        } catch (Exception e) { Alert(T("تعذّر إصلاح الاختصار.","Could not repair the shortcut.") + "\n" + e.Message); }
    }
    void OpenEntry(string path) {
        bool missing = false;
        if (Link(path)) {
            try { WithLink(path, delegate(dynamic link) {
                string target = Environment.ExpandEnvironmentVariables((string)link.TargetPath ?? "");
                missing = Path.IsPathRooted(target) && !File.Exists(target) && !Directory.Exists(target);
            }); } catch (Exception) { /* Shell can still launch special shortcuts. */ }
        }
        if (!missing) {
            try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); return; }
            catch (Exception e) {
                if (!Link(path) && !Url(path)) { Alert(e.Message); return; }
            }
        }
        if (MessageBox.Show(this,T("تعذّر فتح الاختصار. هل تريد تحديد الموقع الجديد؟","Could not open this shortcut. Locate its new target?"),
            "GameDock",MessageBoxButton.YesNo) == MessageBoxResult.Yes) Repair(path);
    }
    static bool SameFolder(string a,string b) {
        return a != null && b != null && Paths.Equals(a.TrimEnd(Path.DirectorySeparatorChar),b.TrimEnd(Path.DirectorySeparatorChar));
    }
    static bool SameShortcut(string a,string b) {
        if (!File.Exists(a) || !File.Exists(b) || !(Link(a) || Url(a))) return false;
        var first = new FileInfo(a);
        if (first.Length != new FileInfo(b).Length || first.Length > 4*1024*1024) return false;
        return File.ReadAllBytes(a).SequenceEqual(File.ReadAllBytes(b));
    }
    static bool DesktopShortcut(string source) {
        return SameFolder(Path.GetDirectoryName(source),DesktopFolder) && (Link(source) || Url(source)) && File.Exists(source);
    }
    string ImportEntry(string source) {
        if (DesktopShortcut(source)) {
            Directory.CreateDirectory(DockStorage);
            return PlaceEntry(source,DockStorage,true,true);
        }
        if (!SameFolder(Path.GetDirectoryName(source),folder)) {
            foreach (string name in HiddenNames()) {
                string original = InFolder(name);
                if (SameShortcut(original,source)) return name;
                if (Link(source) && (File.Exists(original) || Directory.Exists(original)) && !Link(original) && !Url(original)) {
                    string target = "";
                    WithLink(source,delegate(dynamic link) { target = link.TargetPath; });
                    if (Paths.Equals(target,original)) return name;
                }
            }
        }
        return PlaceEntry(source,folder,false);
    }
    static string PlaceEntry(string source,string destination,bool reuse,bool moveShortcut = false) {
        if (SameFolder(Path.GetDirectoryName(source),destination)) return Path.GetFileName(source);
        bool copy = File.Exists(source) && (Link(source) || Url(source));
        if (!File.Exists(source) && !Directory.Exists(source)) throw new FileNotFoundException(source);
        string name = copy ? Path.GetFileName(source) : Path.GetFileName(source) + ".lnk";
        string candidate = Path.Combine(destination,name);
        for (int i = 2; File.Exists(candidate) || Directory.Exists(candidate); i++) {
            if (reuse && copy && SameShortcut(source,candidate)) {
                // The destination already contains a byte-identical shortcut.
                if (moveShortcut) File.Delete(source);
                return Path.GetFileName(candidate);
            }
            if (reuse && !copy && File.Exists(candidate)) {
                string target = "";
                WithLink(candidate,delegate(dynamic link) { target = link.TargetPath; });
                if (Paths.Equals(target,source)) return Path.GetFileName(candidate);
            }
            candidate = Path.Combine(destination,Path.GetFileNameWithoutExtension(name) + " (" + i + ")" + Path.GetExtension(name));
        }
        if (copy && moveShortcut) File.Move(source,candidate);
        else if (copy) File.Copy(source,candidate);
        else WithLink(candidate,delegate(dynamic link) {
            link.TargetPath = source;
            link.WorkingDirectory = Path.GetDirectoryName(source);
            link.Save();
        });
        return Path.GetFileName(candidate);
    }
    [DllImport("user32.dll")] static extern IntPtr WindowFromPoint(System.Drawing.Point point);
    [DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr window,uint flags);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)]
    static extern int GetClassName(IntPtr window,System.Text.StringBuilder name,int capacity);
    static bool OverDesktop() {
        var window = WindowFromPoint(Forms.Cursor.Position);
        var name = new System.Text.StringBuilder(64);
        GetClassName(GetAncestor(window,2),name,name.Capacity);
        return name.ToString() == "Progman" || name.ToString() == "WorkerW";
    }
    void RemoveEntry(string path,bool toDesktop) {
        try {
            string name = host.automatic ? path : Path.GetFileName(path);
            bool hide = true;
            if (toDesktop && !host.automatic) {
                bool shortcut = File.Exists(path) && (Link(path) || Url(path));
                string placed = PlaceEntry(path,DesktopFolder,true,shortcut);
                hide = !shortcut || SameFolder(folder,DesktopFolder);
                if (SameFolder(folder,DesktopFolder) && shortcut) name = placed;
            }
            if (hide) {
                InstalledGame game;
                var keys=host.automatic && host.detected.Games.TryGetValue(name,out game)
                    ? new[]{name,"title:"+GameArtwork.Normalize(game.Title)} : new[]{name};
                var names = HiddenNames().Union(keys,Paths).ToArray();
                Registry.SetValue(HiddenKey,OrderName,names,RegistryValueKind.MultiString);
            }
            Refresh();
        } catch (Exception e) { Alert(T("تعذّر إكمال الإزالة.","Could not remove this item.") + "\n" + e.Message); }
    }
    GameDock draggingDock;
    bool ownDrag;
    Button dragOrigin;
    Point dragStart;
    void EnableDrag(Button tile, string path) {
        bool desktopDrop = false;
        tile.QueryContinueDrag += delegate(object sender, QueryContinueDragEventArgs e) {
            if (ownDrag && !e.EscapePressed && (e.KeyStates & DragDropKeyStates.LeftMouseButton) == 0 && OverDesktop()) {
                desktopDrop = true; e.Action = DragAction.Cancel; e.Handled = true;
            }
        };
        tile.GiveFeedback += delegate(object sender,GiveFeedbackEventArgs e) {
            if (ownDrag && OverDesktop()) { e.UseDefaultCursors = false; Mouse.SetCursor(Cursors.Arrow); e.Handled = true; }
        };
        tile.Tag = host.automatic ? path : Path.GetFileName(path);
        tile.PreviewMouseLeftButtonDown += delegate(object sender, MouseButtonEventArgs e) {
            dragOrigin = tile; dragStart = e.GetPosition(this);
        };
        tile.PreviewMouseMove += delegate(object sender, MouseEventArgs e) {
            if (dragOrigin != tile || e.LeftButton != MouseButtonState.Pressed) return;
            Vector distance = e.GetPosition(this) - dragStart;
            if (Math.Abs(distance.X) < SystemParameters.MinimumHorizontalDragDistance &&
                Math.Abs(distance.Y) < SystemParameters.MinimumVerticalDragDistance) return;
            dragOrigin = null; tile.ReleaseMouseCapture(); ownDrag = true; host.draggingDock = this; desktopDrop = false;
            try { DragDrop.DoDragDrop(tile,new DataObject("GameDock.Item",path),DragDropEffects.Move); }
            finally { host.draggingDock = null; ownDrag = false; ClearMarker(); }
            if (desktopDrop) RemoveEntry(path,true);
        };
        tile.MouseRightButtonUp += delegate(object sender, MouseButtonEventArgs e) {
            e.Handled = true; OpenItemMenu(tile,path);
        };
        tile.KeyDown += delegate(object sender, KeyEventArgs e) {
            if (e.Key == Key.Apps || (e.Key == Key.F10 && Keyboard.Modifiers == ModifierKeys.Shift)) {
                e.Handled = true; OpenItemMenu(tile,path);
            }
        };
    }
    void OpenItemMenu(Button tile,string path) {
        var menu = new ContextMenu { FlowDirection = english ? FlowDirection.LeftToRight : FlowDirection.RightToLeft };
        if (!host.automatic && (Link(path) || Url(path))) {
            var item = new MenuItem { Header = T("إصلاح الاختصار…","Repair shortcut…") };
            item.Click += delegate { Repair(path); };
            menu.Items.Add(item);
        }
        if(host.automatic || Link(path) || Url(path) || Paths.Equals(Path.GetExtension(path),".exe")) {
            InstalledGame game;
            ManualGame manual;
            host.manualGames.TryGetValue(Path.GetFileName(path),out manual);
            host.detected.Games.TryGetValue(path,out game);
            string title=host.automatic && game!=null ? game.Title : manual!=null ? manual.Title : Path.GetFileNameWithoutExtension(path);
            string iconKey=GameArtwork.Normalize(title);
            string id=host.automatic && path.StartsWith("steam-") ? path.Substring(6) : manual==null ? null : manual.SteamId;
            var find=new MenuItem {Header=T("البحث عن أيقونة اللعبة","Find game icon")};
            find.Click+=async delegate {
                var art=await GameArtwork.Get(title,id,true,host.automatic && path=="prism-minecraft" && game!=null ? game.Proof : null);
                if(closing) return;
                if(art==null) {Alert(T("ما لقينا صورة مطابقة الآن. جرّب لاحقًا.","No matching game icon was available. Try again later."));return;}
                try {
                    Registry.SetValue(prefs+@"\Artwork",iconKey,1);
                    Refresh();
                } catch(Exception e) {Alert(e.Message);}
            };
            menu.Items.Add(find);
            if(Object.Equals(ReadSetting(prefs+@"\Artwork",iconKey,0),1)) {
                var restore=new MenuItem {Header=T("استخدام الأيقونة المحلية","Use local icon")};
                restore.Click+=delegate {
                    try {Registry.SetValue(prefs+@"\Artwork",iconKey,0); Refresh();}
                    catch(Exception e) {Alert(e.Message);}
                };
                menu.Items.Add(restore);
            }
        }
        var remove = new MenuItem { Header = T("إزالة من الشريط","Remove from dock") };
        remove.Click += delegate { RemoveEntry(path,false); };
        menu.Items.Add(remove); menu.PlacementTarget = tile; menu.IsOpen = true;
    }
    int marked = -1;
    void Mark(int gap) {
        if (marked == gap) return;
        marked = gap;
        foreach (var pair in visibleTiles) {
            int i = pair.Key;
            var tile = pair.Value;
            var shift = tile.RenderTransform as TranslateTransform;
            if (shift == null) { shift = new TranslateTransform(); tile.RenderTransform = shift; }
            double x = gap < 0 ? 0 : i == gap ? 4 : i == gap-1 ? -4 : 0;
            shift.BeginAnimation(Side ? TranslateTransform.YProperty : TranslateTransform.XProperty,new DoubleAnimation(Side ? shift.Y : shift.X,x,TimeSpan.FromMilliseconds(90)));
        }
    }
    void ClearMarker() { Mark(-1); }
    int Gap(DragEventArgs e) {
        return Math.Max(0,Math.Min(entries.Length,(int)Math.Floor(Along(e.GetPosition(icons)) / Step + 0.5)));
    }
    static DragDropEffects IncomingEffect(DragEventArgs e,bool local) {
        if (local) return e.AllowedEffects & DragDropEffects.Move;
        var files = e.Data.GetData(DataFormats.FileDrop) as string[];
        if (files == null || files.Length == 0) return DragDropEffects.None;
        // Never report an entire mixed batch as moved: regular files remain in place.
        if (files.All(DesktopShortcut)) return e.AllowedEffects & DragDropEffects.Move;
        if (files.Any(DesktopShortcut) && (e.AllowedEffects & DragDropEffects.Move) == 0) return DragDropEffects.None;
        return e.AllowedEffects & DragDropEffects.Copy;
    }
    void Dragging(object sender, DragEventArgs e) {
        bool local = host.draggingDock != null && e.Data.GetDataPresent("GameDock.Item");
        e.Effects = host.automatic && !local ? DragDropEffects.None : IncomingEffect(e,local);
        if (e.Effects != DragDropEffects.None) {
            double x = Along(e.GetPosition(strip));
            if (x < 12 || x > (Side ? strip.ActualHeight : strip.ActualWidth) - 12)
                ScrollTo(ScrollOffset + (x < 12 ? -8 : 8));
            Mark(Gap(e));
        }
        e.Handled = true;
    }
    void Dropped(object sender, DragEventArgs e) {
        ClearMarker(); e.Handled = true;
        bool local = host.draggingDock != null && e.Data.GetDataPresent("GameDock.Item");
        var files = local ? new[] { e.Data.GetData("GameDock.Item") as string } : e.Data.GetData(DataFormats.FileDrop) as string[];
        e.Effects = DragDropEffects.None;
        if (host.automatic && !local) return;
        var effect = IncomingEffect(e,local);
        if (files == null || effect == DragDropEffects.None) return;
        try {
            var order = Entries().ToList();
            int gap = Math.Min(Gap(e),order.Count);
            var added = new System.Collections.Generic.List<string>();
            foreach (string file in files) {
                if (local && !host.automatic && !File.Exists(file) && !Directory.Exists(file)) return;
                string name = local ? (host.automatic ? file : Path.GetFileName(file)) : ImportEntry(file);
                int previous = order.FindIndex(n => Paths.Equals(n,name));
                if (previous >= 0) { order.RemoveAt(previous); if (previous < gap) gap--; }
                if (!added.Contains(name,Paths)) added.Add(name);
            }
            order.InsertRange(gap,added);
            Registry.SetValue(OrderKey,OrderName,order.ToArray(),RegistryValueKind.MultiString);
            var hidden = HiddenNames();
            if (hidden.Length > 0)
                Registry.SetValue(HiddenKey,OrderName,hidden.Except(added,Paths).ToArray(),RegistryValueKind.MultiString);
            e.Effects = effect;
        } catch (Exception ex) { Alert(T("تعذّر إكمال الإفلات.","Could not complete the drop.") + "\n" + ex.Message); }
        Refresh();
    }
    static readonly Dictionary<string,int> genericIcons=new Dictionary<string,int>(StringComparer.OrdinalIgnoreCase);
    static bool GenericIcon(string extension,int index) {
        int generic;
        if(!genericIcons.TryGetValue(extension,out generic)) {
            FileIcon info;
            if(SHGetFileInfo("GameDockIconProbe"+extension,128,out info,(uint)Marshal.SizeOf(typeof(FileIcon)),0x4010)==IntPtr.Zero) return false;
            genericIcons[extension]=generic=info.index;
        }
        return generic==index;
    }
    static BitmapSource GetIcon(string path,bool checkGeneric=false) {
        FileIcon info;
        // Use the base system image, with no shortcut overlay mask.
        IntPtr list = SHGetFileInfo(path, 0, out info, (uint)Marshal.SizeOf(typeof(FileIcon)), 0x4000);
        if (list == IntPtr.Zero) return null;
        if(checkGeneric && (GenericIcon(Path.GetExtension(path),info.index) || GenericIcon(".exe",info.index) ||
            GenericIcon(".lnk",info.index) || GenericIcon(".url",info.index) || GenericIcon(".gamedockunknown",info.index))) return null;
        info.handle = ImageList_GetIcon(list, info.index, 0);
        if (info.handle == IntPtr.Zero) return null;
        try {
            var image = Imaging.CreateBitmapSourceFromHIcon(info.handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            image.Freeze();
            return image;
        } finally { DestroyIcon(info.handle); }
    }
    readonly ControlTemplate tileTemplate = MakeTileTemplate();
    static ControlTemplate MakeTileTemplate() {
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(11));
        border.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding("Background") { RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.TemplatedParent) });
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center);
        presenter.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        border.AppendChild(presenter);
        return new ControlTemplate(typeof(Button)) { VisualTree = border };
    }
    ToolTip sharedTip;
    TextBlock tipLabel;
    ToolTip MakeTip() {
        var tip = new ToolTip { Placement = PlacementMode.Custom, HasDropShadow = false };
        tip.CustomPopupPlacementCallback = delegate(Size bubbleSize, Size tileSize, Point offset) {
            double center = (tileSize.Width - bubbleSize.Width) / 2;
            Point anchor = Side
                ? new Point(edge == Dock.Left ? tileSize.Width + 4 : -bubbleSize.Width - 4,(tileSize.Height-bubbleSize.Height)/2)
                : new Point(center,edge == Dock.Top ? tileSize.Height + 4 : -bubbleSize.Height - 4);
            return new[] { new CustomPopupPlacement(anchor, Side ? PopupPrimaryAxis.Vertical : PopupPrimaryAxis.Horizontal) };
        };
        tipLabel = new TextBlock { FontFamily = new FontFamily("Segoe UI"), FontWeight = FontWeights.SemiBold,
            TextAlignment = TextAlignment.Center, FontSize = 12, MaxWidth = 360, TextTrimming = TextTrimming.CharacterEllipsis };
        var bubble = new Border { CornerRadius = new CornerRadius(9), BorderThickness = new Thickness(1),
            Padding = new Thickness(11,5,11,6), Child = tipLabel };
        var tipPresenter = new FrameworkElementFactory(typeof(ContentPresenter));
        tipPresenter.SetBinding(ContentPresenter.ContentProperty, new System.Windows.Data.Binding("Content") {
            RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.TemplatedParent) });
        tip.Template = new ControlTemplate(typeof(ToolTip)) { VisualTree = tipPresenter };
        tip.Content = bubble;
        tip.Opened += delegate {
            bubble.Background = theme == 2
                ? (Brush)new SolidColorBrush(CaptionColor(colors[0].Rgb(255)))
                : theme == 0 ? Color("#F0272727") : panelPaint;
            bubble.BorderBrush = outlinePaint; tipLabel.Foreground = labelPaint;
        };
        return tip;
    }
    Button Tile(object content, string name, Action action, bool lift = false) {
        if (sharedTip == null) sharedTip = MakeTip();
        var tile = new Button { Width = Slot, Height = TileHeight, Content = content, ToolTip = sharedTip,
            Background = Brushes.Transparent, Foreground = Brushes.White, BorderThickness = new Thickness(0),
            Cursor = Cursors.Hand, Padding = new Thickness(0) };
        tile.ToolTipOpening += delegate { tipLabel.Text = name; };
        tile.Template = tileTemplate;
        ToolTipService.SetPlacement(tile, PlacementMode.Custom);
        ToolTipService.SetPlacementTarget(tile, tile);
        ToolTipService.SetInitialShowDelay(tile, 220);
        var element = content as FrameworkElement;
        if (lift && element != null) {
            var shift = new TranslateTransform();
            element.RenderTransform = shift;
            tile.MouseEnter += delegate { Lift(shift, -4); };
            tile.MouseLeave += delegate { Lift(shift, 0); };
        }
        tile.MouseEnter += delegate { tile.Background = Color("#32FFFFFF"); };
        tile.MouseLeave += delegate { tile.Background = Brushes.Transparent; };
        System.Windows.Automation.AutomationProperties.SetName(tile, name);
        tile.Click += delegate { action(); };
        return tile;
    }
    void Lift(TranslateTransform transform, double amount) {
        double end = edge == Dock.Top || edge == Dock.Left ? -amount : amount;
        var animation = new DoubleAnimation(Side ? transform.X : transform.Y, end, TimeSpan.FromMilliseconds(155)) {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        transform.BeginAnimation(Side ? TranslateTransform.XProperty : TranslateTransform.YProperty, animation);
    }
    static StackPanel Dots() {
        var dots = new StackPanel { Orientation = Orientation.Horizontal, RenderTransform = new TranslateTransform(0,-2) };
        for (int i = 0; i < 3; i++)
            dots.Children.Add(new System.Windows.Shapes.Ellipse { Width = 3.5, Height = 3.5,
                Margin = new Thickness(1.2,0,1.2,0), Fill = Color("#FFFFFF") });
        return dots;
    }
    void ReleaseTray() {
        if (tray == null) return;
        tray.Visible = false; tray.Dispose(); tray = null;
        logo.Dispose(); logo = null;
    }
    void CreateTray() {
        tray = new Forms.NotifyIcon();
        var assembly = System.Reflection.Assembly.GetExecutingAssembly();
        using (var source = assembly.GetManifestResourceStream("GameDock.WhiteGlyph.ico")) {
            var size = Forms.SystemInformation.SmallIconSize;
            using (var selected = new System.Drawing.Icon(source,size))
                logo = (System.Drawing.Icon)selected.Clone();
        }
        tray.Icon = logo;
        tray.MouseClick += delegate(object sender, Forms.MouseEventArgs e) {
            if (e.Button == Forms.MouseButtons.Left || e.Button == Forms.MouseButtons.Right) OpenSettings();
        };
    }
    void ApplyAccess() {
        access.Children.Clear();
        if (useTray && host == this) {
            if (tray == null) CreateTray();
            tray.Text = T("GameDock — الإعدادات", "GameDock — Settings");
            tray.Visible = true;
        } else ReleaseTray();
        access.Visibility = useTray ? Visibility.Collapsed : Visibility.Visible;
        if (!useTray) {
            var button = Tile(Dots(), T("الإعدادات", "Settings"), OpenSettings);
            if (Side) button.Height = 33; else button.Width = 33;
            access.Children.Add(button);
            access.Children.Add(new Border { Width = Side ? 24 : 1, Height = Side ? 1 : 24, Background = Color("#60DDDDDD"), Margin = Side ? new Thickness(0,2,0,6) : new Thickness(2,0,6,0) });
        }
        Fit();
    }
    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
    void SyncColors() {
        if (settingsWindow == null) return;
        const string personalize = @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
        bool light = !Object.Equals(ReadSetting(personalize, "AppsUseLightTheme", 1), 0);
        IntPtr handle = new WindowInteropHelper(settingsWindow).Handle;
        if (handle != IntPtr.Zero) {
            int dark = light ? 0 : 1;
            if (DwmSetWindowAttribute(handle,20,ref dark,4) != 0)
                DwmSetWindowAttribute(handle,19,ref dark,4);
        }
        var r = settingsWindow.Resources;
        r["Panel"] = Color(light ? "#FFFFFF" : "#202020");
        r["Text"] = Color(light ? "#202020" : "#F5F5F5");
        r["Button"] = Color(light ? "#EFEFEF" : "#303030");
        r["Selected"] = Color(light ? "#FFFFFF" : "#F4F4F4");
        r["ActiveText"] = Color("#202020");
        r["ActiveStroke"] = Color(light ? "#666666" : "#FFFFFF");
        r["ActiveHover"] = Color("#E7E7E7");
        r["Rail"] = Color(light ? "#777777" : "#AAAAAA");
        r["ThumbStroke"] = Color(light ? "#555555" : "#777777");
        r["Hover"] = Color(light ? "#E2E2E2" : "#414141");
    }
    void WindowsPreferenceChanged(object sender, UserPreferenceChangedEventArgs e) {
        if (!Dispatcher.HasShutdownStarted) Dispatcher.BeginInvoke(new Action(SyncColors));
    }
    Button SettingButton(string caption, Action click, bool selected) {
        var btn = new Button { Content = caption, Margin = new Thickness(2), Padding = new Thickness(10,6,10,6),
            MinHeight = 33, Cursor = Cursors.Hand };
        btn.SetResourceReference(Control.ForegroundProperty, selected ? "ActiveText" : "Text");
        btn.SetResourceReference(Control.BackgroundProperty, selected ? "Selected" : "Button");
        var frame = new FrameworkElementFactory(typeof(Border));
        frame.SetValue(Border.CornerRadiusProperty, new CornerRadius(6));
        frame.SetValue(Border.BorderThicknessProperty,new Thickness(1));
        if (selected) frame.SetResourceReference(Border.BorderBrushProperty,"ActiveStroke");
        frame.SetValue(Border.PaddingProperty, new Thickness(10,6,10,6));
        frame.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding("Background") {
            RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.TemplatedParent) });
        var content = new FrameworkElementFactory(typeof(ContentPresenter));
        content.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center);
        frame.AppendChild(content);
        btn.Template = new ControlTemplate(typeof(Button)) { VisualTree = frame };
        btn.MouseEnter += delegate { btn.SetResourceReference(Control.BackgroundProperty,selected ? "ActiveHover" : "Hover"); };
        btn.MouseLeave += delegate { btn.SetResourceReference(Control.BackgroundProperty,selected ? "Selected" : "Button"); };
        btn.Click += delegate { click(); };
        return btn;
    }
    void Section(StackPanel body, string title, string[] choices, int current, Action<int> select) {
        body.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, Margin = new Thickness(2,10,2,3) });
        var row = new WrapPanel();
        for (int i = 0; i < choices.Length; i++) {
            int choice = i;
            row.Children.Add(SettingButton(choices[i], delegate { select(choice); DrawSettings(); }, i == current));
        }
        body.Children.Add(row);
    }
    // Keep the slider functional through WPF's standard Track, Thumb and routed commands.
    static ControlTemplate sliderTemplate;
    static ControlTemplate NeutralSlider { get { return sliderTemplate ?? (sliderTemplate = (ControlTemplate)System.Windows.Markup.XamlReader.Parse(
        @"<ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'
            xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' TargetType='{x:Type Slider}'>
            <Grid Height='22' Background='Transparent'>
              <Border Height='3' Margin='6,0' CornerRadius='1.5' Background='{DynamicResource Rail}'/>
              <Track x:Name='PART_Track' Orientation='Horizontal'>
                <Track.Resources>
                  <Style TargetType='{x:Type RepeatButton}'>
                    <Setter Property='Focusable' Value='False'/>
                    <Setter Property='Template'><Setter.Value><ControlTemplate TargetType='{x:Type RepeatButton}'>
                      <Border Background='Transparent'/>
                    </ControlTemplate></Setter.Value></Setter>
                  </Style>
                </Track.Resources>
                <Track.DecreaseRepeatButton><RepeatButton Command='{x:Static Slider.DecreaseLarge}'/></Track.DecreaseRepeatButton>
                <Track.Thumb><Thumb Width='16' Height='16'><Thumb.Template><ControlTemplate TargetType='{x:Type Thumb}'>
                  <Ellipse Fill='White' Stroke='{DynamicResource ThumbStroke}' StrokeThickness='1.5'/>
                </ControlTemplate></Thumb.Template></Thumb></Track.Thumb>
                <Track.IncreaseRepeatButton><RepeatButton Command='{x:Static Slider.IncreaseLarge}'/></Track.IncreaseRepeatButton>
              </Track>
            </Grid>
          </ControlTemplate>")); } }
    void ColorSlider(StackPanel body,string title,string unit,int maximum,int value,Action<int> update) {
        var label = new TextBlock { Text = title+"  "+value+unit, Margin = new Thickness(2,4,2,0) };
        var slider = new Slider { Template = NeutralSlider, Minimum=0, Maximum=maximum, Value=value, SmallChange=1,
            LargeChange=10, TickFrequency=1, IsSnapToTickEnabled=true };
        System.Windows.Automation.AutomationProperties.SetName(slider,title);
        slider.ValueChanged += delegate {
            int next = (int)Math.Round(slider.Value);
            label.Text = title+"  "+next+unit; update(next);
        };
        body.Children.Add(label); body.Children.Add(slider);
    }
    void ColorEditor(StackPanel body) {
        Section(body,T("تعبئة الشريط","Dock fill"),new[] {T("لون واحد","One color"),T("تدرّج بلونين","Two-color gradient")},
            gradient ? 1 : 0,delegate(int i) { gradient = i == 1; activeColor = 0; SavePreference("Gradient",i); ApplyTheme(); });
        if (gradient)
            Section(body,T("تعديل اللون","Edit color"),new[] {T("اللون الأول","First color"),T("اللون الثاني","Second color")},
                activeColor,delegate(int i) { activeColor = i; });
        var preview = new Border { Height=15, CornerRadius=new CornerRadius(6), Margin=new Thickness(2,7,2,2), Background=panelPaint };
        body.Children.Add(preview);
        var c = colors[gradient ? activeColor : 0];
        Action changed = delegate {
            ApplyTheme(); preview.Background=panelPaint;
            SavePreference(c.Key+"Hue",c.Hue); SavePreference(c.Key+"Saturation",c.Saturation);
            SavePreference(c.Key+"Brightness",c.Brightness);
        };
        ColorSlider(body,T("درجة اللون","Hue"),"°",360,c.Hue,delegate(int n) { c.Hue=n; changed(); });
        ColorSlider(body,T("التشبّع","Saturation"),"%",100,c.Saturation,delegate(int n) { c.Saturation=n; changed(); });
        ColorSlider(body,T("السطوع","Brightness"),"%",100,c.Brightness,delegate(int n) { c.Brightness=n; changed(); });
    }
    void ApplyTopmost(bool enabled) {
        Topmost=enabled;
        foreach(var dock in mirrors) dock.Topmost=enabled;
        if(settingsWindow!=null) settingsWindow.Topmost=enabled;
    }
    void AddSwitch(StackPanel body,string label,bool enabled,Action<bool> changed) {
        var switchRow=new DockPanel {Margin=new Thickness(2,10,2,3)};
        var knob=new Border {Width=14,Height=14,CornerRadius=new CornerRadius(7),Margin=new Thickness(3),Background=Brushes.White};
        var track=new Border {Width=36,Height=20,CornerRadius=new CornerRadius(10),Child=knob};
        var toggle=new ToggleButton {Content=track,IsChecked=enabled,VerticalAlignment=VerticalAlignment.Center,
            Padding=new Thickness(0),BorderThickness=new Thickness(0),Cursor=Cursors.Hand,ToolTip=label};
        var presenter=new FrameworkElementFactory(typeof(ContentPresenter));
        toggle.Template=new ControlTemplate(typeof(ToggleButton)){VisualTree=presenter};
        System.Windows.Automation.AutomationProperties.SetName(toggle,label);
        Action paint=delegate {
            knob.HorizontalAlignment=enabled ? HorizontalAlignment.Right : HorizontalAlignment.Left;
            track.Background=enabled ? Color("#217B66") : Color("#737780");
        };
        paint();
        toggle.Click+=delegate {
            enabled=toggle.IsChecked==true;
            changed(enabled); paint();
        };
        DockPanel.SetDock(toggle,Dock.Right); switchRow.Children.Add(toggle);
        switchRow.Children.Add(new TextBlock {Text=label,VerticalAlignment=VerticalAlignment.Center});
        body.Children.Add(switchRow);
    }
    int settingsPage;
    void DrawSettings() {
        var w = settingsWindow;
        w.Title = T("إعدادات GameDock", "GameDock settings");
        w.FlowDirection = english ? FlowDirection.LeftToRight : FlowDirection.RightToLeft;

        var layout=new DockPanel {Margin=new Thickness(14,10,14,12)};
        var nav=new UniformGrid {Rows=1,Margin=new Thickness(0,0,0,8)};
        string[] pages={T("الألعاب","Games"),T("الشريط","Dock"),T("المظهر","Appearance"),T("عام","General")};
        for(int i=0;i<pages.Length;i++) {
            int page=i;
            var tab=SettingButton(pages[i],delegate {settingsPage=page;DrawSettings();},page==settingsPage);
            nav.Children.Add(tab);
        }
        DockPanel.SetDock(nav,Dock.Top); layout.Children.Add(nav);
        var body=new StackPanel {Margin=new Thickness(6,0,6,6)};
        switch(settingsPage) {
            case 0: DrawLibrarySettings(body); break;
            case 1: DrawDockSettings(body); break;
            case 2: DrawAppearanceSettings(body); break;
            default: DrawGeneralSettings(body); break;
        }
        layout.Children.Add(new ScrollViewer {VerticalScrollBarVisibility=ScrollBarVisibility.Auto,Content=body});
        w.Content=layout;
    }
    void DrawDockSettings(StackPanel body) {
        AddDisplaySetting(body);
        AddSwitch(body,T("دائمًا في المقدّمة","Always on top"),Topmost,delegate(bool enabled) {
            ApplyTopmost(enabled);
            SavePreference("AlwaysOnTop",enabled ? 1 : 0);
        });
        AddSwitch(body,T("إظهار الشريط في Alt+Tab","Show dock in Alt+Tab"),showInSwitcher,delegate(bool enabled) {
            showInSwitcher=enabled; SetSwitcher(this,enabled);
            SavePreference("ShowInSwitcher",enabled ? 1 : 0);
        });
        Section(body,T("مكان الشريط","Dock position"),
            new[] { T("يسار","Left"),T("فوق","Top"),T("يمين","Right"),T("تحت","Bottom") },(int)edge,
            delegate(int i) {
                if (i == (int)edge) return;
                double index = ScrollOffset / Step;
                edge = (Dock)i; SavePreference("DockEdge",i);
                SetEdgeLayout(); Refresh(); UpdateLayout(); ScrollTo(index * Step); Position();
            });
    }
    void DrawGeneralSettings(StackPanel body) {
        Section(body, T("مكان الإعدادات","Settings access"),
            new[] { T("⋯ على الشريط","⋯ On the dock"), T("أيقونة بجانب الساعة","System tray icon") }, useTray ? 1 : 0,
            delegate(int i) { useTray = i == 1; ApplyAccess(); SavePreference("TrayAccess", i); });
        Section(body, T("اللغة","Language"), new[] { "العربية", "English" }, english ? 1 : 0,
            delegate(int i) { english = i == 1; SavePreference("English",i); ApplyAccess(); Refresh(); });
        bool starts = StartsWithWindows();
        body.Children.Add(SettingButton(T("تشغيل مع ويندوز","Start with Windows") + (starts ? "  ✓" : ""),
            delegate { SetStartup(); DrawSettings(); }, starts));
        body.Children.Add(SettingButton(T("خروج من التطبيق","Quit GameDock"), Close, false));
    }
    void DrawAppearanceSettings(StackPanel body) {
        Section(body, T("شكل الشريط","Dock appearance"), new[] { T("شفاف","Transparent"),T("أسود","Black"),T("ملوّن","Color") }, theme,
            delegate(int i) { theme = i; ApplyTheme(); SavePreference("Theme",i); });
        if (theme == 2) ColorEditor(body);
        var count = new TextBlock { Margin = new Thickness(2,10,2,4) };
        string prefix = T("عدد الأيقونات الظاهرة · ","Visible icons · ");
        count.Text = prefix + visibleCount; body.Children.Add(count);
        var slider = new Slider { Template = NeutralSlider, Minimum = 1, Maximum = 24, Value = visibleCount, TickFrequency = 1, IsSnapToTickEnabled = true };
        slider.ValueChanged += delegate { visibleCount = (int)Math.Round(slider.Value); count.Text = prefix + visibleCount;
            StopScroll(); Fit(); SavePreference("VisibleCount",visibleCount); };
        body.Children.Add(slider);
    }
    void DrawLibrarySettings(StackPanel body) {
        Section(body,T("مصدر الألعاب","Game source"),new[]{T("اكتشاف تلقائي","Automatic discovery"),T("مجلد يدوي","Manual folder")},automatic ? 0 : 1,delegate(int i) {
            if (automatic == (i==0)) return;
            if (i==1 && !Directory.Exists(folder)) { Choose(); if (!Directory.Exists(folder)) return; }
            automatic = i==0;
            SavePreference("AutomaticGames",automatic ? 1 : 0);
            if (automatic) { if (watcher!=null) { watcher.Dispose(); watcher=null; } Refresh(); ScanGames(); }
            else { StopGameWatchers(); Bind(); }
        });
        var files = new WrapPanel { Margin = new Thickness(0,12,0,3) };
        AddSwitch(body,T("تنزيل أيقونات الألعاب تلقائيًا","Download game icons automatically"),downloadIcons,delegate(bool on) {
            downloadIcons=on; SavePreference("AutoDownloadIcons",on ? 1 : 0); Refresh();
        });
        if(automatic) Section(body,T("عند فتح Minecraft من Prism","When opening Minecraft with Prism"),
            new[]{T("اختيار النسخة","Choose a profile"),T("آخر نسخة لعبتها","Last played profile")},lastMinecraftProfile ? 1 : 0,delegate(int i) {
                lastMinecraftProfile=i==1; SavePreference("LastMinecraftProfile",i);
            });
        files.Children.Add(SettingButton(T("فتح المجلد","Open folder"), delegate { Launch(folder); }, false));
        files.Children.Add(SettingButton(T("تغيير المجلد","Change folder"), Choose, false));
        if (automatic) body.Children.Add(SettingButton(T("تحديث قائمة الألعاب","Refresh detected games"),ScanGames,false));
        else body.Children.Add(files);
        if ((automatic || Directory.Exists(folder)) && HiddenNames().Length > 0)
            body.Children.Add(SettingButton(T("إظهار العناصر المخفية","Restore hidden items"),delegate {
                Registry.SetValue(HiddenKey,OrderName,new string[0],RegistryValueKind.MultiString);
                Refresh(); DrawSettings();
            },false));
    }
    void OpenSettings() {
        host.settingsAnchor = this;
        host.ShowSettings();
    }
    void ShowSettings() {
        if (settingsWindow != null) { PositionSettings(); settingsWindow.Activate(); return; }
        var w = new Window { Owner = this, Topmost = this.Topmost, Width = Math.Min(410,settingsAnchor.WorkArea.Width), Height=Math.Min(378,settingsAnchor.WorkArea.Height-12),
            MaxHeight = settingsAnchor.WorkArea.Height - 12, ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.Manual, FontFamily = new FontFamily("Segoe UI") };
        using (var source = System.Reflection.Assembly.GetExecutingAssembly().GetManifestResourceStream("GameDock.WhiteGlyph.ico")) {
            var frame = BitmapFrame.Create(source,BitmapCreateOptions.PreservePixelFormat,BitmapCacheOption.OnLoad);
            frame.Freeze(); w.Icon = frame;
        }

        settingsWindow = w;
        SyncColors();
        w.SetResourceReference(Window.BackgroundProperty, "Panel");
        w.SetResourceReference(Window.ForegroundProperty, "Text");
        w.SizeChanged += delegate { PositionSettings(); };
        w.SourceInitialized += delegate { SetSwitcher(w,false); SyncColors(); };
        w.Activated += delegate { SyncColors(); };
        SystemEvents.UserPreferenceChanged += WindowsPreferenceChanged;
        w.Closed += delegate { SystemEvents.UserPreferenceChanged -= WindowsPreferenceChanged;
            settingsWindow = null; sliderTemplate = null; };
        DrawSettings(); w.Show(); w.Activate();
    }
    GameDock(GameDock owner = null, Forms.Screen display = null) {
        host = owner ?? this;
        Topmost = owner==null ? Preference("AlwaysOnTop",1,0,1)==1 : owner.Topmost;
        ShowActivated=false;
        screen = display ?? Forms.Screen.PrimaryScreen;
        settingsAnchor = this;
        SourceInitialized += delegate { SetSwitcher(this,host==this && showInSwitcher); };
        Title = "GameDock"; WindowStyle = WindowStyle.None; AllowsTransparency = true;
        Background = Brushes.Transparent; ResizeMode = ResizeMode.NoResize; ShowInTaskbar = false;
        SizeToContent = SizeToContent.WidthAndHeight;
        theme = Preference("Theme", 0, 0, 2);
        visibleCount = Preference("VisibleCount", 12, 1, 24);
        english = Preference("English",0,0,1) == 1;
        useTray = Preference("TrayAccess",0,0,1) == 1;
        strip.ScrollChanged += delegate { ShowVisibleTiles(); };
        strip.Content = icons;
        row.Children.Add(access);
        SetEdgeLayout();
        row.Children.Add(strip);
        shell.BorderThickness = new Thickness(1); shell.CornerRadius = new CornerRadius(18);
        shell.Padding = new Thickness(6,4,6,4); shell.Child = row;
        Content = shell; ApplyTheme();
        AllowDrop = true; PreviewDragOver += Dragging; PreviewDrop += Dropped;
        DragLeave += delegate { ClearMarker(); };
        PreviewMouseWheel += delegate(object sender, MouseWheelEventArgs e) {
            StartScroll(e.Delta); e.Handled = true;
        };
        timer.Tick += delegate {
            timer.Stop();
            System.Threading.Interlocked.Exchange(ref folderRefreshPending,0);
            if (!closing) { if (automatic) ScanGames(); else Refresh(); }
        };
        Loaded += delegate {
            if (host != this) return;
            try { if (File.Exists(settings)) folder = File.ReadAllText(settings); } catch (IOException) {}
            if (automatic) { Refresh(); ArrangeDisplays(); ScanGames(); return; }
            if (!Directory.Exists(folder)) Choose();
            else Bind();
            if (watcher == null && !Directory.Exists(folder)) Close();
            else ArrangeDisplays();
        };
        SizeChanged += delegate { Position(); };
        if (host == this) {
            SystemParameters.StaticPropertyChanged += SystemChanged;
            SystemEvents.DisplaySettingsChanged += DisplayChanged;
            System.Net.NetworkInformation.NetworkChange.NetworkAvailabilityChanged+=NetworkChanged;
        }
        Closed += delegate {
            closing = true; ReleaseTray(); StopGameWatchers();
            if (host == this) {
                SystemEvents.DisplaySettingsChanged -= DisplayChanged;
                System.Net.NetworkInformation.NetworkChange.NetworkAvailabilityChanged-=NetworkChanged;
                foreach (var mirror in mirrors.ToArray()) mirror.Close();
                mirrors.Clear();
            }
            StopScroll(); SystemParameters.StaticPropertyChanged -= SystemChanged; timer.Stop(); if (watcher != null) watcher.Dispose(); };
    }
    void SystemChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e) {
        if (e.PropertyName == "WorkArea") DisplayChanged(sender, EventArgs.Empty);
    }
    void Position() {
        var area = WorkArea;
        Left = edge == Dock.Left ? area.Left + 8 : edge == Dock.Right ? area.Right - ActualWidth - 8 : area.Left + (area.Width - ActualWidth)/2;
        Top = edge == Dock.Top ? area.Top + 8 : edge == Dock.Bottom ? area.Bottom - ActualHeight - 8 : area.Top + (area.Height - ActualHeight)/2;
        PositionSettings();
    }
    void NetworkChanged(object sender,System.Net.NetworkInformation.NetworkAvailabilityEventArgs e) {
        if(!e.IsAvailable || Dispatcher.HasShutdownStarted) return;
        GameArtwork.NetworkRestored();
        Dispatcher.BeginInvoke(new Action(delegate {if(!closing && downloadIcons && IsLoaded) Refresh();}));
    }
    void PositionSettings() {
        var w = settingsWindow;
        if (w == null || w.ActualWidth == 0 || w.ActualHeight == 0) return;
        var anchor = settingsAnchor;
        var area = anchor.WorkArea;
        w.Width = Math.Min(410, area.Width);
        w.MaxHeight = Math.Max(1, area.Height - 12);
        double Left = anchor.Left, Top = anchor.Top, ActualWidth = anchor.ActualWidth, ActualHeight = anchor.ActualHeight;
        double x = edge == Dock.Left ? Left + ActualWidth + 6 : edge == Dock.Right ? Left - w.ActualWidth - 6 : Left + (ActualWidth-w.ActualWidth)/2;
        double y = edge == Dock.Top ? Top + ActualHeight + 6 : edge == Dock.Bottom ? Top - w.ActualHeight - 6 : Top + (ActualHeight-w.ActualHeight)/2;
        w.Left = Math.Max(area.Left,Math.Min(area.Right-w.ActualWidth,x));
        w.Top = Math.Max(area.Top,Math.Min(area.Bottom-w.ActualHeight,y));
    }
    void Fit() {
        var area = WorkArea;
        double available = (Side ? area.Height : area.Width) - (useTray ? 30 : 76);
        int count = Math.Max(1,Math.Min(visibleCount,(int)(available / Step)));
        strip.MaxWidth = Side ? Double.PositiveInfinity : count * Step;
        strip.MaxHeight = Side ? count * Step : Double.PositiveInfinity;
    }
    void Choose() {
        if (host != this) { host.Choose(); return; }
        using (var dialog = new Forms.FolderBrowserDialog()) {
            dialog.Description = T("اختر المجلد الذي يحتوي على اختصارات الألعاب والملفات", "Choose the folder containing your games and shortcuts");
            dialog.SelectedPath = Directory.Exists(folder) ? folder : Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            if (dialog.ShowDialog() != Forms.DialogResult.OK) return;
            folder = dialog.SelectedPath;
        }
        try { Directory.CreateDirectory(Path.GetDirectoryName(settings)); File.WriteAllText(settings, folder); }
        catch (Exception e) { Alert(T("تعذّر حفظ اختيار المجلد.", "Could not save your folder.") + "\n" + e.Message); }
        Bind(); ScrollTo(0);
    }
    void Changed(object sender, EventArgs e) {
        // Reserve one UI wakeup for the entire batch, including watcher overflow.
        if (System.Threading.Interlocked.Exchange(ref folderRefreshPending,1) != 0) return;
        Dispatcher.BeginInvoke(new Action(delegate { if (!closing) timer.Start(); }));
    }
    void Bind() {
        if (watcher != null) { watcher.Dispose(); watcher = null; }
        try {
            watcher = new FileSystemWatcher(folder);
            watcher.NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Attributes;
            watcher.Created += Changed; watcher.Deleted += Changed; watcher.Renamed += Changed; watcher.Changed += Changed;
            watcher.Error += Changed;
            watcher.EnableRaisingEvents = true;
        } catch (Exception e) { Alert(T("تعذّرت مراقبة المجلد. اختره مجدداً من القائمة.", "Could not monitor this folder. Choose it again in settings.") + "\n" + e.Message); }
        Refresh();
    }
    Grid GameVisual(string title,string icon,string steamId,string minecraftJar=null,string shortcutIcon=null,string gameExe=null) {
        var box=new Grid {Width=32,Height=32,ClipToBounds=true};
        BitmapSource art=null;
        string source=null;
        foreach(string file in new[]{shortcutIcon,icon,gameExe}.Where(p=>!String.IsNullOrEmpty(p)).Distinct(Paths)) {
            if(!GameArtwork.IsGameIconFile(file,title,Paths.Equals(file,gameExe))) continue;
            try {art=Paths.Equals(Path.GetExtension(file),".ico") ? GameArtwork.ReadIconFile(file) : GetIcon(file,true);}
            catch(ArgumentException) {} catch(COMException) {}
            if(art!=null) {source=file; break;}
        }
        if(art!=null) {
            box.Children.Add(new Image {Source=art});
        } else {
            box.Children.Add(new TextBlock {Text=String.IsNullOrWhiteSpace(title)?"?":System.Globalization.StringInfo.GetNextTextElement(title),
                HorizontalAlignment=HorizontalAlignment.Center,VerticalAlignment=VerticalAlignment.Center,FontSize=19});
        }
        RoutedEventHandler loaded=null;
        loaded=async delegate {
                box.Loaded-=loaded;
                if(String.IsNullOrWhiteSpace(title)) return;
                bool repaired=Object.Equals(ReadSetting(prefs+@"\Artwork",GameArtwork.Normalize(title),0),1);
                if(art!=null) await GameArtwork.RememberLocal(title,steamId,source,art);
                if(art!=null && !repaired) return;
                BitmapSource fallback=null;
                if(repaired) fallback=await GameArtwork.Get(title,steamId,false,null,false);
                if(fallback==null && art==null) {
                    fallback=await GameArtwork.LastLocal(title,steamId);
                    if(fallback==null) fallback=await GameArtwork.Get(title,steamId,false,minecraftJar,host.downloadIcons);
                }
                if(fallback!=null && box.IsLoaded) {
                    box.Children.Clear();
                    box.Children.Add(new Image {Source=fallback,Stretch=Stretch.Uniform});
                }
            };
        box.Loaded+=loaded;
        return box;
    }
    Button EntryTile(int index) {
        string entry = entries[index];
        if (host.automatic) {
            InstalledGame game;
            if (!host.detected.Games.TryGetValue(entry,out game)) return new Button();
            string verifiedExe=(entry.StartsWith("riot-") || entry.StartsWith("gog-") || entry.StartsWith("xbox-") ||
                entry.StartsWith("epic-") || entry.StartsWith("steam-") || entry.StartsWith("uplay-")) ? game.Proof : null;
            var content=GameVisual(game.Title,game.Icon,entry.StartsWith("steam-",StringComparison.Ordinal) ? entry.Substring(6) : null,entry=="prism-minecraft" ? game.Proof : null,game.ShortcutIcon,verifiedExe);
            var button=Tile(content,game.Title,delegate {
                if(!GameScan.Ready(game)) {host.ScanGames();return;}
                Launch(game.Target,game.LaunchArguments(host.lastMinecraftProfile));
            },true);
            EnableDrag(button,entry);
            Canvas.SetLeft(button,Side ? 0 : index*(double)Slot);
            Canvas.SetTop(button,Side ? index*(double)TileHeight : 0);
            return button;
        }
        string path = InFolder(entry);
        ManualGame manual;
        bool isGame=host.manualGames.TryGetValue(entry,out manual);
        string title=isGame ? manual.Title : entry;
        object visual;
        if(isGame) visual=GameVisual(title,manual.Icon,manual.SteamId,null,null,manual.TargetIcon);
        else {
            var image=GetIcon(path);
            visual=image==null ? (object)new TextBlock {Text="?"} : new Image {Source=image,Width=32,Height=32};
        }
        var tile=Tile(visual,title,delegate {OpenEntry(path);},true);
        EnableDrag(tile,path);
        Canvas.SetLeft(tile,Side ? 0 : index*(double)Slot);
        Canvas.SetTop(tile,Side ? index*(double)TileHeight : 0);
        return tile;
    }
    void ShowVisibleTiles() {
        if (entries.Length == 0) return;
        double viewport = Side ? strip.ViewportHeight : strip.ViewportWidth;
        double limit = Side ? strip.MaxHeight : strip.MaxWidth;
        viewport = viewport <= 0 ? limit : Math.Min(viewport,limit);
        double offset = Math.Min(ScrollOffset,Math.Max(0,entries.Length*(double)Step-viewport));
        int first = Math.Max(0,(int)Math.Floor(offset/Step)-1);
        int last = Math.Min(entries.Length-1,(int)Math.Ceiling((offset+viewport)/Step));
        if (first == firstTile && last == lastTile) return;
        firstTile = first; lastTile = last;
        foreach (int i in visibleTiles.Keys.Where(i => i < first || i > last).ToArray()) {
            icons.Children.Remove(visibleTiles[i]); visibleTiles.Remove(i);
        }
        for (int i=first;i<=last;i++) {
            if (visibleTiles.ContainsKey(i)) continue;
            var tile = EntryTile(i); visibleTiles.Add(i,tile); icons.Children.Add(tile);
        }
    }
    void Refresh() {
        if (host != this) { host.Refresh(); return; }
        bool failed = false;
        string[] list;
        try { list = Entries(); } catch (Exception) { list = new string[0]; failed = true; }
        RenderEntries(list, failed);
        foreach (var dock in mirrors) { dock.folder = folder; dock.RenderEntries(list, failed); }
    }
    void RenderEntries(string[] list, bool failed) {
        entries = list;
        StopScroll();
        double offset = ScrollOffset;
        dragOrigin = null; firstTile = lastTile = -1; visibleTiles.Clear(); icons.Children.Clear(); Fit();
        int count = Math.Max(1,entries.Length);
        icons.Width = Side ? Slot : Slot*(double)count;
        icons.Height = Side ? TileHeight*(double)count : TileHeight;
        if (entries.Length != 0) ShowVisibleTiles();
        else if (host.automatic) icons.Children.Add(Tile("🎮", T("لم يُعثر على ألعاب مدعومة. اضغط للتحديث","No supported games found. Click to refresh"),host.ScanGames));
        else if (failed) icons.Children.Add(Tile("…", T("تعذّرت قراءة المجلد. اختر مجلداً آخر", "Folder unavailable. Choose another folder"), Choose));
        else {
            var add = Tile("+", T("افتح المجلد وأضف اختصاراتك", "Open folder to add shortcuts"), delegate { Launch(folder); });
            add.FontSize = 24; icons.Children.Add(add);
        }
        ScrollTo(offset);
    }
    string Command { get { return "\"" + System.Reflection.Assembly.GetExecutingAssembly().Location + "\""; } }
    bool StartsWithWindows() {
        return String.Equals(ReadSetting(@"HKEY_CURRENT_USER\" + runKey, "GameDock", "") as string, Command, StringComparison.OrdinalIgnoreCase);
    }
    void SetStartup() {
        try {
            bool remove = StartsWithWindows();
            using (var key = Registry.CurrentUser.CreateSubKey(runKey)) {
                if (remove) key.DeleteValue("GameDock", false);
                else key.SetValue("GameDock", Command);
            }
        } catch (Exception e) { Alert(T("تعذّر تغيير إعداد بدء التشغيل.", "Could not change the startup setting.") + "\n" + e.Message); }
    }
    [DllImport("shell32.dll",CharSet=CharSet.Unicode)]
    static extern void SHChangeNotify(int eventId,uint flags,string path,IntPtr unused);
    [STAThread] static void Main(string[] args) {
        if (args.Contains("--refresh-icon"))
            SHChangeNotify(0x2000,0x0005,System.Reflection.Assembly.GetExecutingAssembly().Location,IntPtr.Zero);
        bool first;
        using (var mutex = new System.Threading.Mutex(true, @"Local\GameDock.FolderDock.1", out first)) {
            if (!first) return;
            new Application().Run(new GameDock());
        }
    }
}
