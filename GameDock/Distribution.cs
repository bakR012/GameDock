using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Collections.Generic;
using Microsoft.Win32;
using F = System.Windows.Forms;
using D = System.Drawing;

[assembly: System.Reflection.AssemblyTitle("GameDock")]
[assembly: System.Reflection.AssemblyProduct("GameDock")]
[assembly: System.Reflection.AssemblyVersion("1.7.2.0")]
[assembly: System.Reflection.AssemblyFileVersion("1.7.2.0")]

internal sealed class Distribution : F.Form {
    const string Arp = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\GameDock";
    const string InstallSub = @"Software\GameDock\Installation";
    const string RunSub = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string Maintenance = "GameDock-Uninstall.exe";

    const string InstallKey = @"HKEY_CURRENT_USER\Software\GameDock\Installation";
    const string RunKey = @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Run";

    readonly bool arabic = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ar";
    readonly F.TextBox directory = new F.TextBox();
    readonly F.CheckBox desktop = new F.CheckBox(), start = new F.CheckBox(), launch = new F.CheckBox();
    readonly F.Button install = new F.Button(), browse = new F.Button();
    readonly F.Label status = new F.Label();
    readonly string previous = Registry.GetValue(InstallKey,"Path","") as string ?? "";
    bool busy;
    string T(string ar,string en) { return arabic ? ar : en; }
    static string Quote(string path) { return "\"" + path + "\""; }
    static string Exe(string directory) { return Path.Combine(directory,"GameDock.exe"); }
    static bool Equal(string a,string b) { return String.Equals(a,b,StringComparison.OrdinalIgnoreCase); }
    static string OldExe { get { return Exe(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"GameDock")); } }
    static bool Running() {
        try { using (Mutex.OpenExisting(@"Local\GameDock.FolderDock.1")) return true; }
        catch (WaitHandleCannotBeOpenedException) { return false; }
    }
    [STAThread] public static void Main(string[] args) {
        F.Application.EnableVisualStyles(); F.Application.SetCompatibleTextRenderingDefault(false);
        try {
            if(args.Contains("--uninstall")) { BeginRemove(); return; }
            if(args.Length==2 && args[0]=="--finish-remove") { FinishRemove(args[1]); return; }
            bool first;
            using(var gate=new Mutex(true,@"Local\GameDock.Setup",out first)) {
                if(first) F.Application.Run(new Distribution());
            }
        } catch(Exception e) { F.MessageBox.Show(e.Message,"GameDock Setup"); }
    }
    Distribution() {
        Text = T("تثبيت GameDock","GameDock Setup");
        Font = new D.Font("Segoe UI",10); BackColor = D.Color.White;
        AutoScaleMode=F.AutoScaleMode.Dpi; ClientSize=new D.Size(480,342);
        FormBorderStyle=F.FormBorderStyle.FixedDialog; MaximizeBox=false; StartPosition=F.FormStartPosition.CenterScreen;
        if(arabic) { RightToLeft=F.RightToLeft.Yes; RightToLeftLayout=true; }
        Icon=D.Icon.ExtractAssociatedIcon(Self);
        var heading=new F.Label { Text="GameDock", Font=new D.Font("Segoe UI",20,D.FontStyle.Bold), AutoSize=true, Left=22, Top=15 };
        Controls.Add(heading);
        var label=new F.Label { Text=T("مجلد التثبيت","Installation folder"), AutoSize=true, Left=24,Top=78 };
        Controls.Add(label); directory.SetBounds(24,104,329,28); directory.RightToLeft=F.RightToLeft.No;
        directory.Text=previous.Length==0 ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Programs","GameDock") : previous.TrimEnd('\\');
        browse.Text=T("استعراض…","Browse…"); browse.SetBounds(358,102,98,32);
        browse.Click+=delegate { using(var d=new F.FolderBrowserDialog { SelectedPath=directory.Text,Description=label.Text }) {
            if(d.ShowDialog(this)==F.DialogResult.OK) directory.Text=d.SelectedPath;
        }};
        Controls.Add(directory);Controls.Add(browse);
        if(previous.Length!=0) { directory.ReadOnly=true; browse.Enabled=false; }
        Option(desktop,T("إنشاء اختصار على سطح المكتب","Create a desktop shortcut"),151,previous.Length==0 || File.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),"GameDock.lnk")));
        string run=Registry.GetValue(RunKey,"GameDock","") as string;
        Option(start,T("التشغيل عند تسجيل الدخول","Start GameDock when I sign in"),182,Equal(run,Quote(OldExe)) || Equal(run,Quote(Exe(directory.Text))));
        Option(launch,T("فتح GameDock بعد التثبيت","Open GameDock after installation"),213,true);
        status.SetBounds(24,249,432,35); Controls.Add(status);
        install.Text=T("تثبيت","Install"); install.SetBounds(349,289,107,34); Controls.Add(install);
        var cancel=new F.Button {Text=T("إلغاء","Cancel"),Left=239,Top=289,Width=102,Height=34,DialogResult=F.DialogResult.Cancel};
        Controls.Add(cancel); CancelButton=cancel; AcceptButton=install;
        install.Click+=delegate { Install(); }; FormClosing+=delegate(object sender,F.FormClosingEventArgs e) { if(busy) e.Cancel=true; };
    }
    void Option(F.CheckBox box,string text,int top,bool value) {
        box.Text=text; box.SetBounds(24,top,432,29); box.Checked=value; Controls.Add(box);
    }
    void Install() {
        if(Running()) { F.MessageBox.Show(this,T("اختر «خروج من التطبيق» في GameDock ثم اضغط تثبيت مجدداً.","Quit GameDock from its settings, then click Install again."),Text); return; }
        string target;
        try {
            target=Path.GetFullPath(directory.Text.Trim()).TrimEnd('\\');
            if(!Path.IsPathRooted(directory.Text.Trim()) || target.Length<=3 || target.IndexOf('"')>=0)
                throw new IOException(T("اختر مجلداً خاصاً بالتطبيق.","Choose a folder for GameDock."));
            // Protect unrelated executables already present at a chosen destination.
            if(File.Exists(Exe(target)) && previous.Length==0 && !Equal(Exe(target),OldExe))
                throw new IOException(T("هذا المجلد يحتوي على GameDock.exe. اختر مجلداً آخر.","This folder already contains GameDock.exe. Choose a different folder."));
        } catch(Exception e) { F.MessageBox.Show(this,e.Message,Text); return; }
        busy=true; Enabled=false; status.Text=T("جارٍ تثبيت GameDock…","Installing GameDock…"); Refresh();
        try {
            InstallFiles(target);
            if(launch.Checked) Process.Start(new ProcessStartInfo(Exe(target)){UseShellExecute=true,WorkingDirectory=target});
            busy=false; F.MessageBox.Show(this,T("تم تثبيت GameDock. ستجده في قائمة ابدأ.","GameDock is installed. You can find it in the Start menu."),Text); Close();
        } catch(Exception e) { F.MessageBox.Show(this,e.Message,Text); }
        finally { busy=false; if(!IsDisposed) {Enabled=true;status.Text="";} }
    }
    static string Self { get { return System.Reflection.Assembly.GetExecutingAssembly().Location; } }
    static string LinkPath(Environment.SpecialFolder place) {
        return Path.Combine(Environment.GetFolderPath(place),"GameDock.lnk");
    }
    static string ReadPath() {
        string value=Registry.GetValue(InstallKey,"Path","") as string;
        return String.IsNullOrEmpty(value) ? "" : Path.GetFullPath(value).TrimEnd('\\');
    }
    static string Shortcut(string file,string target) {
        dynamic shell=Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell",true));
        try {
            dynamic link=shell.CreateShortcut(file);
            try {
                if(target==null) return (string)link.TargetPath;
                link.TargetPath=target; link.WorkingDirectory=Path.GetDirectoryName(target);
                link.IconLocation=target+",0"; link.Save(); return target;
            } finally { Marshal.FinalReleaseComObject(link); }
        } finally { Marshal.FinalReleaseComObject(shell); }
    }
    sealed class Changes : IDisposable {
        readonly Stack<Action> undo=new Stack<Action>();
        readonly List<string> backups=new List<string>();
        bool committed;
        public void Replace(string file,string source) {
            string temp=file+"."+Guid.NewGuid().ToString("N")+".tmp";
            try {
                if(source!=null) File.Copy(source,temp);
                if(File.Exists(file)) {
                    string backup=temp+".bak";
                    if(source==null) File.Move(file,backup);
                    else File.Replace(temp,file,backup);
                    backups.Add(backup);
                    undo.Push(delegate { if(File.Exists(file)) File.Delete(file); File.Move(backup,file); });
                } else if(source!=null) {
                    File.Move(temp,file); undo.Push(delegate { File.Delete(file); });
                }
            } finally { if(File.Exists(temp)) File.Delete(temp); }
        }
        public void Value(string sub,string name,object value) {
            using(var key=Registry.CurrentUser.CreateSubKey(sub)) {
                object old=key.GetValue(name,null,RegistryValueOptions.DoNotExpandEnvironmentNames);
                var kind=old==null ? RegistryValueKind.String : key.GetValueKind(name);
                if(value==null) key.DeleteValue(name,false); else key.SetValue(name,value);
                undo.Push(delegate {
                    using(var k=Registry.CurrentUser.CreateSubKey(sub)) {
                        if(old==null) k.DeleteValue(name,false); else k.SetValue(name,old,kind);
                    }
                });
            }
        }
        public void Commit() { committed=true; }
        public void Dispose() {
            if(!committed) { while(undo.Count>0) undo.Pop()(); }
            else foreach(string backup in backups) try { File.Delete(backup); } catch(IOException) {}
        }
    }
    void SetLink(Changes change,string path,string exe,bool enabled) {
        if(File.Exists(path)) {
            string target=Shortcut(path,null);
            if(!Equal(target,exe) && !Equal(target,OldExe)) {
                if(enabled) throw new IOException(T("يوجد اختصار آخر بنفس الاسم: ","An unrelated shortcut already exists: ")+path);
                return;
            }
        }
        if(!enabled) { change.Replace(path,null); return; }
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        string temp=Path.Combine(Path.GetTempPath(),Guid.NewGuid().ToString("N")+".lnk");
        try { Shortcut(temp,exe); change.Replace(path,temp); } finally { File.Delete(temp); }
    }
    void InstallFiles(string target) {
        Directory.CreateDirectory(target);
        string exe=Exe(target), helper=Path.Combine(target,Maintenance);
        if(File.Exists(helper) && !Equal(ReadPath(),target)) throw new IOException(helper);
        using(var tx=new Changes()) {
            string payload = Path.Combine(target, Guid.NewGuid().ToString("N") + ".tmp");
            try {
                using (var input = typeof(Distribution).Assembly.GetManifestResourceStream("GameDock.exe"))
                using (var output = new FileStream(payload, FileMode.CreateNew)) input.CopyTo(output);
                tx.Replace(exe, payload);
            } finally { File.Delete(payload); }
            tx.Replace(helper,Self);
            SetLink(tx,LinkPath(Environment.SpecialFolder.Programs),exe,true);
            SetLink(tx,LinkPath(Environment.SpecialFolder.DesktopDirectory),exe,desktop.Checked);
            string run=Registry.GetValue(RunKey,"GameDock","") as string;
            if(start.Checked) tx.Value(RunSub,"GameDock",Quote(exe));
            else if(Equal(run,Quote(exe)) || Equal(run,Quote(OldExe))) tx.Value(RunSub,"GameDock",null);
            tx.Value(InstallSub,"Path",target);
            tx.Value(Arp,"DisplayName","GameDock");
            tx.Value(Arp,"DisplayVersion","1.7.2");
            tx.Value(Arp,"Publisher","GameDock"); tx.Value(Arp,"InstallLocation",target);
            tx.Value(Arp,"DisplayIcon",Quote(exe)+",0");
            tx.Value(Arp,"NoModify",1); tx.Value(Arp,"NoRepair",1);
            tx.Value(Arp,"UninstallString",Quote(helper)+" --uninstall");
            tx.Commit();
        }
    }
    static string Locale(string ar,string en) { return System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName=="ar" ? ar : en; }
    static void BeginRemove() {
        string dir=ReadPath();
        if(dir.Length==0 || !Equal(Self,Path.Combine(dir,Maintenance))) return;
        if(Running()) { F.MessageBox.Show(Locale("أغلق GameDock من إعداداته ثم أعد الإزالة.","Quit GameDock from its settings, then try again."),"GameDock"); return; }
        if(F.MessageBox.Show(Locale("تريد إزالة GameDock؟ ستبقى ألعابك وإعداداتك محفوظة.","Remove GameDock? Your games and preferences will be kept."),"GameDock",F.MessageBoxButtons.YesNo)!=F.DialogResult.Yes) return;
        string temp=Path.Combine(Path.GetTempPath(),"GameDock-Remove-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        string copy=Path.Combine(temp,Maintenance); File.Copy(Self,copy);
        Process.Start(new ProcessStartInfo(copy,"--finish-remove "+Process.GetCurrentProcess().Id){UseShellExecute=false,WorkingDirectory=temp});
    }
    static void FinishRemove(string pidText) {
        string temp=Path.GetDirectoryName(Self);
        if(!Equal(Path.GetDirectoryName(temp),Path.GetTempPath().TrimEnd('\\')) || !Path.GetFileName(temp).StartsWith("GameDock-Remove-")) return;
        try {
            int pid=int.Parse(pidText);
            try { using(var parent=Process.GetProcessById(pid)) { if(!parent.WaitForExit(10000)) throw new IOException("Setup is busy."); } } catch(ArgumentException) {}
            bool first;
            using(var gate=new Mutex(true,@"Local\GameDock.Setup",out first)) {
                if(!first || Running()) throw new IOException(Locale("أغلق GameDock ثم أعد الإزالة.","Close GameDock, then try again."));
                string dir=ReadPath(); if(dir.Length==0) return;
                string exe=Exe(dir), helper=Path.Combine(dir,Maintenance);
                string owner=Registry.GetValue(@"HKEY_CURRENT_USER\"+Arp,"UninstallString","") as string;
                if(!Equal(owner,Quote(helper)+" --uninstall")) throw new IOException("GameDock installation changed.");
                using(var tx=new Changes()) {
                    foreach(var place in new[]{Environment.SpecialFolder.Programs,Environment.SpecialFolder.DesktopDirectory}) {
                        string link=LinkPath(place);
                        if(File.Exists(link) && Equal(Shortcut(link,null),exe)) tx.Replace(link,null);
                    }
                    tx.Replace(exe,null); tx.Replace(helper,null);
                    if(Equal(Registry.GetValue(RunKey,"GameDock","") as string,Quote(exe))) tx.Value(RunSub,"GameDock",null);
                    tx.Value(InstallSub,"Path",null);
                    Registry.CurrentUser.DeleteSubKeyTree(Arp,false); tx.Commit();
                }
                if(Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir);
            }
        } finally {
            // Let the helper exit before releasing its own temporary executable.
            var clean=new ProcessStartInfo(Path.Combine(Environment.SystemDirectory,"cmd.exe"),
                "/d /v:off /c for /L %i in (1,1,8) do @if exist GameDock-Uninstall.exe (del /q GameDock-Uninstall.exe 2>nul & if exist GameDock-Uninstall.exe \"%SystemRoot%\\System32\\ping.exe\" -n 2 127.0.0.1 >nul)"){
                UseShellExecute=false,CreateNoWindow=true,WorkingDirectory=temp };
            Process.Start(clean);
        }
    }
}
