using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;
using System.Xml.Linq;
using Microsoft.Win32;

internal sealed class InstalledGame {
    internal string Title, Target, Icon, ShortcutIcon, Arguments, Proof, Manifest, PickerArguments;
    internal string LaunchArguments(bool lastProfile) {
        return !lastProfile && PickerArguments!=null ? PickerArguments : Arguments;
    }
}

internal sealed class ManualGame {
    internal string Title, Icon, TargetIcon, SteamId;
    internal bool LauncherOnly;

    internal static ManualGame Read(string path,object shortcutShell) {
        var title=Path.GetFileNameWithoutExtension(path);
        var game=new ManualGame {Title=title};
        string target=path, args="", icon=null;
        string ext=Path.GetExtension(path).ToLowerInvariant();
        try {
            if(ext==".lnk" && shortcutShell!=null) {
                dynamic link=((dynamic)shortcutShell).CreateShortcut(path);
                try {
                    target=(string)link.TargetPath??"";
                    args=(string)link.Arguments??"";
                    string spec=(string)link.IconLocation??"";
                    int comma=spec.LastIndexOf(',');
                    icon=comma<0 ? spec : spec.Substring(0,comma);
                } finally {System.Runtime.InteropServices.Marshal.FinalReleaseComObject(link);}
            } else if(ext==".url") {
                foreach(string line in File.ReadLines(path).Take(80)) {
                    if(line.StartsWith("URL=",StringComparison.OrdinalIgnoreCase)) target=line.Substring(4).Trim();
                    if(line.StartsWith("IconFile=",StringComparison.OrdinalIgnoreCase)) icon=line.Substring(9);
                }
                var steam=Regex.Match(target,@"^steam://(?:run|rungameid)/(\d+)/?$",RegexOptions.IgnoreCase);
                if(steam.Success) game.SteamId=steam.Groups[1].Value;
            }
            bool prism=GameScan.IsPrism(target);
            bool profile=prism && Regex.IsMatch(args,@"(?:^|\s)(?:--launch|-l)\s+(?:""[^""]+""|[^\s""]+)",RegexOptions.IgnoreCase);
            if(profile && GameScan.IsPrism(title)) game.Title="Minecraft";
            game.LauncherOnly=(prism && !profile) ||
                (String.IsNullOrWhiteSpace(args) && (GameScan.IsLauncher(target) || GameScan.IsLauncher(title)) && game.SteamId==null);
            if(!game.LauncherOnly && String.IsNullOrWhiteSpace(args) && GameArtwork.IsGameIconFile(target,game.Title))
                game.TargetIcon=game.Icon=target;
            // Shell icons often belong to the application that opens the shortcut.
            if(!String.IsNullOrWhiteSpace(icon)) {
                icon=Environment.ExpandEnvironmentVariables(icon.Trim().Trim('"'));
                if(!Path.IsPathRooted(icon)) icon=Path.Combine(Path.GetDirectoryName(path),icon);
                game.Icon=ShortcutIcon(icon,target,args,game.Title) ?? game.Icon;
            }
        } catch(IOException) {} catch(UnauthorizedAccessException) {} catch(ArgumentException) {}
          catch(System.Runtime.InteropServices.COMException) {}
        return game;
    }
    internal static string ShortcutIcon(string icon,string target,string args,string title) {
        if(String.IsNullOrWhiteSpace(icon) || !String.Equals(Path.GetExtension(icon),".ico",StringComparison.OrdinalIgnoreCase) ||
           !File.Exists(icon) || new FileInfo(icon).Length==0) return null;
        // A launcher may assign its own badge to every shortcut it creates.
        if(!String.IsNullOrWhiteSpace(args) && Path.IsPathRooted(target) &&
           String.Equals(Path.GetDirectoryName(icon),Path.GetDirectoryName(target),StringComparison.OrdinalIgnoreCase) &&
           GameArtwork.Normalize(Path.GetFileNameWithoutExtension(icon))!=GameArtwork.Normalize(title)) return null;
        return icon;
    }
}

internal sealed class GameScan {
    internal readonly Dictionary<string,InstalledGame> Games = new Dictionary<string,InstalledGame>(StringComparer.OrdinalIgnoreCase);
    internal readonly HashSet<string> Watches = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    internal bool Retry;

    void Watch(string directory, string filter) {
        while (!Directory.Exists(directory)) {
            string parent = Path.GetDirectoryName(directory);
            if (String.IsNullOrEmpty(parent)) return;
            filter = Path.GetFileName(directory);
            directory = parent;
        }
        Watches.Add(directory + "|" + filter);
    }
    void Try(Action action) {
        try { action(); }
        catch (System.Runtime.InteropServices.COMException) {}
        catch (IOException) { Retry = true; }
        catch (UnauthorizedAccessException) {}
        catch (System.Security.SecurityException) {}
        catch (FormatException) {}
        catch (ArgumentException) {}
        catch (InvalidOperationException) {}
        catch (System.Xml.XmlException) {}
    }
    static string Child(string root, string relative) {
        if (String.IsNullOrWhiteSpace(root) || !Path.IsPathRooted(root) || String.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative)) return null;
        string prefix = Path.GetFullPath(root).TrimEnd('\\') + "\\";
        string path = Path.GetFullPath(Path.Combine(prefix,relative));
        return path.StartsWith(prefix,StringComparison.OrdinalIgnoreCase) ? path : null;
    }
    internal readonly Dictionary<string,string> Aliases = new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
    internal string Resolve(string id) {
        string matched;
        return Aliases.TryGetValue(id,out matched) ? matched : id;
    }
    static string ProgramName(string file) {
        if(String.IsNullOrWhiteSpace(file) || file.IndexOf("://",StringComparison.Ordinal)>=0) return "";
        string name=Path.GetFileName(file);
        if(name.EndsWith(".exe",StringComparison.OrdinalIgnoreCase)) name=Path.GetFileNameWithoutExtension(name);
        return GameArtwork.Normalize(name);
    }
    internal static bool IsPrism(string file) {
        string name=ProgramName(file);
        return name=="prismlauncher" || name=="multimc";
    }
    internal static bool IsLauncher(string file) {
        return Regex.IsMatch(ProgramName(file),@"^(?:steam|epicgameslauncher|riotclientservices|prismlauncher|multimc|minecraftlauncher|ubisoftconnect|upc|battlenet|galaxyclient|eadesktop|explorer|ankerlauncher|ankergameslauncher)$");
    }
    static bool Utility(string file) {
        string name=Path.GetFileNameWithoutExtension(file);
        return IsLauncher(file) || Regex.IsMatch(name,@"^(?:unins\d*|uninstall.*|setup(?:[._-].*)?|install(?:er)?|updater|crashreport.*|crashhandler.*|unitycrash.*|UE(?:4|5)Prereq.*|EasyAntiCheat.*|vc_redist.*|dxsetup|prismlauncher|multimc|minecraftlauncher|riotclientservices|steam|epicgameslauncher)$",RegexOptions.IgnoreCase);
    }
    static bool Excluded(string title) {
        return Regex.IsMatch(title,@"(?:\bPBE\b|public (?:beta|test) (?:environment|server)|\bdedicated server\b|\b(?:mod|map) editor\b|(?:minecraft|prism|multimc)\s*launcher\b)",RegexOptions.IgnoreCase);
    }
    static int SourceRank(string id) {
        if(id.StartsWith("steam-") || id.StartsWith("epic-") || id.StartsWith("riot-") || id.StartsWith("xbox-") || id.StartsWith("gog-") || id=="prism-minecraft") return 0;
        return id.StartsWith("anker-") ? 1 : 2;
    }
    internal void Consolidate() {
        var names=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
        var proofs=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
        foreach(var pair in Games.OrderBy(p=>SourceRank(p.Key)).ThenBy(p=>p.Key,StringComparer.OrdinalIgnoreCase).ToArray()) {
            var game=pair.Value;
            if(String.IsNullOrWhiteSpace(game.Title) || Excluded(game.Title) || !File.Exists(game.Proof)) {Games.Remove(pair.Key);continue;}
            string key=GameArtwork.Normalize(game.Title), winner;
            if(key.Length==0) {Games.Remove(pair.Key);continue;}
            if(names.TryGetValue(key,out winner) || proofs.TryGetValue(game.Proof,out winner)) {
                if(Games[winner].ShortcutIcon==null && String.Equals(Games[winner].Proof,game.Proof,StringComparison.OrdinalIgnoreCase))
                    Games[winner].ShortcutIcon=game.ShortcutIcon;
                Aliases[pair.Key]=winner; Games.Remove(pair.Key);
            } else winner=pair.Key;
            names[key]=winner; proofs[game.Proof]=winner;
            Watch(Path.GetDirectoryName(game.Proof),Path.GetFileName(game.Proof));
        }
    }
    internal void Riot(string root) {
        string clients = Path.Combine(root,"RiotClientInstalls.json");
        Watch(root,"RiotClientInstalls.json");
        string metadata = Path.Combine(root,"Metadata");
        Watch(metadata,"*");
        if (!File.Exists(clients) || !Directory.Exists(metadata)) return;
        var settings = new JavaScriptSerializer().Deserialize<Dictionary<string,object>>(File.ReadAllText(clients));
        if (settings == null) return;
        string client = Text(settings,"rc_default");
        if (!File.Exists(client) || !String.Equals(Path.GetFileName(client),"RiotClientServices.exe",StringComparison.OrdinalIgnoreCase)) return;
        var products = new Dictionary<string,string> {{"league_of_legends","League of Legends"},{"valorant","VALORANT"},{"teamfighttactics","Teamfight Tactics"},{"bacon","Legends of Runeterra"},{"lion","2XKO"}};
        foreach (string dir in Directory.GetDirectories(metadata)) Try(delegate {
            string name = Path.GetFileName(dir);
            string[] parts = name.Split('.');
            string title;
            if (parts.Length!=2 || !products.TryGetValue(parts[0],out title) || parts[1]!="live") return;
            string manifest = name+".product_settings.yaml";
            Watch(dir,manifest);
            string file = Path.Combine(dir,manifest);
            if (!File.Exists(file)) return;
            var line = File.ReadLines(file).FirstOrDefault(l=>l.StartsWith("product_install_full_path:",StringComparison.Ordinal));
            if (line==null) return;
            string location = line.Substring(line.IndexOf(':')+1).Trim().Trim('"');
            string[] binaries;
            switch(parts[0]) {
                case "league_of_legends": binaries=new[]{@"Game\League of Legends.exe"}; break;
                case "teamfighttactics": binaries=new[]{"TFTClient.exe"}; break;
                case "valorant": binaries=new[]{@"ShooterGame\Binaries\Win64\VALORANT-Win64-Shipping.exe",@"live\ShooterGame\Binaries\Win64\VALORANT-Win64-Shipping.exe"}; break;
                case "lion": binaries=new[]{@"Lion\Binaries\Win64\Lion-Win64-Shipping.exe","2XKO.exe"}; break;
                default: binaries=new[]{"LoR.exe","Legends of Runeterra.exe"}; break;
            }
            string proof=binaries.Select(p=>Child(location,p)).FirstOrDefault(File.Exists);
            if(proof==null) return;
            string productIcon=Path.Combine(dir,name+".ico");
            Watch(dir,name+".ico");
            Games["riot-"+parts[0]] = new InstalledGame {Title=title,Proof=proof,Manifest=file,Target=client,Icon=File.Exists(productIcon)?productIcon:proof,Arguments="--launch-product="+parts[0]+" --launch-patchline=live"};
        });
    }
    // A Ubisoft shortcut can outlive its game. Require a registered install and an executable.
    void UbisoftShortcut(string file,string title) {
        string line=File.ReadLines(file).Take(80).FirstOrDefault(l=>l.StartsWith("URL=",StringComparison.OrdinalIgnoreCase));
        var match=Regex.Match(line??"", @"^URL=uplay://launch/(\d+)(?:/\d+)?/?$",RegexOptions.IgnoreCase);
        if(!match.Success || Excluded(title)) return;
        string id=match.Groups[1].Value;
        foreach(var view in new[]{RegistryView.Registry32,RegistryView.Registry64}) {
            using(var hive=RegistryKey.OpenBaseKey(RegistryHive.LocalMachine,view))
            using(var install=hive.OpenSubKey(@"SOFTWARE\Ubisoft\Launcher\Installs\"+id)) {
                if(install==null) continue;
                string root=install.GetValue("InstallDir") as string;
                if(!Directory.Exists(root)) continue;
                foreach(string rel in new[]{"","bin",@"bin\x64",@"Binaries\Win64"}) {
                    string dir=Path.Combine(root,rel);
                    if(!Directory.Exists(dir)) continue;
                    foreach(var exe in Directory.EnumerateFiles(dir,"*.exe")) {
                        string name=Path.GetFileNameWithoutExtension(exe);
                        if(Utility(exe) || (GameArtwork.Normalize(name)!=GameArtwork.Normalize(title) &&
                            GameArtwork.Normalize(System.Diagnostics.FileVersionInfo.GetVersionInfo(exe).ProductName)!=GameArtwork.Normalize(title))) continue;
                        Games["uplay-"+id]=new InstalledGame {Title=title,Target="uplay://launch/"+id+"/0",Proof=exe,Icon=exe};
                        Aliases[line.Substring(4)]="uplay-"+id;
                        return;
                    }
                }
            }
        }
    }
    internal void Shortcuts(string directory, bool curated) {
        if (String.IsNullOrWhiteSpace(directory)) return;
        Watch(directory,"*.lnk");
        Watch(directory,"*.url");
        if(curated) Watch(directory,"*.exe");
        if (!Directory.Exists(directory)) return;
        dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell"));
        try {
            foreach (var file in Directory.EnumerateFiles(directory)) Try(delegate {
                string ext = Path.GetExtension(file).ToLowerInvariant();
                string title = Path.GetFileNameWithoutExtension(file);
                string target = file, args = "", id = null, candidateIcon=null;
                if (ext == ".url") {
                    UbisoftShortcut(file,title);
                    var manual=ManualGame.Read(file,null);
                    InstalledGame installed;
                    if(manual.SteamId!=null && Games.TryGetValue("steam-"+manual.SteamId,out installed) && installed.ShortcutIcon==null)
                        installed.ShortcutIcon=manual.Icon;
                    return;
                }
                if (ext == ".lnk") {
                    dynamic link = shell.CreateShortcut(file);
                    string spec;
                    try { target = link.TargetPath; args = link.Arguments ?? ""; spec=link.IconLocation ?? ""; }
                    finally { System.Runtime.InteropServices.Marshal.FinalReleaseComObject(link); }
                    int comma=spec.LastIndexOf(',');
                    string candidate=Environment.ExpandEnvironmentVariables((comma<0?spec:spec.Substring(0,comma)).Trim().Trim('"'));
                    if(candidate.Length>0 && !Path.IsPathRooted(candidate)) candidate=Path.Combine(directory,candidate);
                    string chosen=ManualGame.ShortcutIcon(candidate,target,args,title);
                    if(chosen!=null) {
                        foreach(var installed in Games.Values) {
                            bool sameExe=String.Equals(target,installed.Proof,StringComparison.OrdinalIgnoreCase);
                            bool sameLaunch=String.Equals(target,installed.Target,StringComparison.OrdinalIgnoreCase) &&
                                String.Equals(args.Trim(),(installed.Arguments??"").Trim(),StringComparison.OrdinalIgnoreCase);
                            if((sameExe || sameLaunch) && installed.ShortcutIcon==null) installed.ShortcutIcon=chosen;
                        }
                    }
                    candidateIcon=chosen;
                } else if (ext != ".exe" || !curated) return;
                // A store URL proves ownership of a shortcut, not installation of the game.
                if (!File.Exists(target) || !String.Equals(Path.GetExtension(target),".exe",StringComparison.OrdinalIgnoreCase)) return;
                if (Utility(target) || Excluded(title)) return;
                string exe=Path.GetFileNameWithoutExtension(target), parent=Path.GetDirectoryName(target);
                bool unity=File.Exists(Path.Combine(parent,"UnityPlayer.dll")) && Directory.Exists(Path.Combine(parent,exe+"_Data"));
                bool unreal=Regex.IsMatch(target,@"\\Binaries\\Win(?:32|64)\\[^\\]+-Shipping\.exe$",RegexOptions.IgnoreCase);
                bool godot=File.Exists(Path.ChangeExtension(target,".pck")) && (System.Diagnostics.FileVersionInfo.GetVersionInfo(target).ProductName??"").IndexOf("Godot",StringComparison.OrdinalIgnoreCase)>=0;
                if(!unity && !unreal && !godot) return;
                id=target+" "+args;
                Games[id]=new InstalledGame {Title=title,Target=file,Proof=target,ShortcutIcon=candidateIcon};
            });
        } finally { System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell); }
    }
    static bool RealFolder(string path) {
        return Directory.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint)==0;
    }
    static string ConfigValue(string path,string key,string fallback) {
        if (!File.Exists(path)) return fallback;
        string line=File.ReadLines(path).FirstOrDefault(l=>l.StartsWith(key+"=",StringComparison.OrdinalIgnoreCase));
        return line==null ? fallback : line.Substring(key.Length+1).Trim();
    }
    internal void Prism(string exe,string home) {
        Watch(Path.GetDirectoryName(exe),Path.GetFileName(exe));
        if (!File.Exists(exe)) return;
        string config=Path.Combine(home,"prismlauncher.cfg");
        Watch(home,"prismlauncher.cfg");
        string root=Path.GetFullPath(Path.Combine(home,ConfigValue(config,"InstanceDir","instances")));
        Watch(root,"*");
        long latest=Int64.MinValue;
        string best=null, jar=null;
        var instances=new List<string>();
        if (Directory.Exists(root)) foreach (string dir in Directory.GetDirectories(root)) Try(delegate {
            if (!RealFolder(dir)) return;
            string cfg=Path.Combine(dir,"instance.cfg");
            Watch(dir,"instance.cfg"); Watch(dir,"mmc-pack.json");
            if (!File.Exists(cfg) || !File.Exists(Path.Combine(dir,"mmc-pack.json"))) return;
            string id=Path.GetFileName(dir);
            var pack=new JavaScriptSerializer().Deserialize<Dictionary<string,object>>(File.ReadAllText(Path.Combine(dir,"mmc-pack.json")));
            object rows;
            if(pack==null || !pack.TryGetValue("components",out rows) || !(rows is System.Collections.IList)) return;
            var mc=((System.Collections.IList)rows).OfType<Dictionary<string,object>>().FirstOrDefault(c=>Text(c,"uid")=="net.minecraft");
            string version=mc==null ? "" : Text(mc,"version");
            if(!Regex.IsMatch(version,@"^[a-z0-9._-]+$",RegexOptions.IgnoreCase)) return;
            string candidate=Path.Combine(home,@"libraries\com\mojang\minecraft",version,"minecraft-"+version+"-client.jar");
            Watch(Path.GetDirectoryName(candidate),Path.GetFileName(candidate));
            if(!File.Exists(candidate)) return;
            long last; Int64.TryParse(ConfigValue(cfg,"lastLaunchTime","0"),out last);
            instances.Add(dir);
            if(last>latest || (last==latest && String.CompareOrdinal(id,best)<0)) {best=id; latest=last; jar=candidate;}
        });
        if(best!=null) {
            string picker="--dir \""+home.TrimEnd('\\')+"\"";
            Games["prism-minecraft"]=new InstalledGame {Title="Minecraft",Target=exe,Proof=jar,PickerArguments=picker,Arguments=picker+" --launch \""+best+"\""};
            foreach(var dir in instances) Aliases["prism:"+dir]="prism-minecraft";
        }
    }
    void AddExternal(string id,string title,string exe) {
        if (String.IsNullOrWhiteSpace(title) || Games.ContainsKey(id) || Utility(exe)) return;
        Games[id]=new InstalledGame {Title=title,Target=exe,Proof=exe};
    }
    internal void Anker(string data) {
        string library=Path.Combine(data,"library.json");
        Watch(data,"library.json");
        if (!File.Exists(library)) return;
        var parser=new JavaScriptSerializer {MaxJsonLength=32*1024*1024};
        var doc=parser.Deserialize<Dictionary<string,object>>(File.ReadAllText(library));
        object raw;
        if(doc==null || !doc.TryGetValue("games",out raw) || !(raw is Dictionary<string,object>)) return;
        foreach(var pair in (Dictionary<string,object>)raw) Try(delegate {
            var item=pair.Value as Dictionary<string,object>;
            if(item==null) return;
            string root=Text(item,"root");
            if(root.StartsWith(@"\\?\")) root=root.Substring(4);
            if(!Directory.Exists(root) || !File.Exists(Path.Combine(root,".anker-installation.json"))) return;
            object options;
            var names=new List<string>{Text(item,"executable")};
            if(item.TryGetValue("launch_options",out options) && options is System.Collections.IEnumerable && !(options is string)) names.AddRange(((System.Collections.IEnumerable)options).Cast<object>().OfType<string>());
            var dirs=new List<string>{root};
            dirs.AddRange(Directory.GetDirectories(root).Where(RealFolder));
            foreach(string name in names.Where(n=>n.EndsWith(".exe",StringComparison.OrdinalIgnoreCase))) foreach(string dir in dirs) {
                string exe=Child(dir,name);
                if(File.Exists(exe)) { AddExternal("anker-"+pair.Key,Text(item,"title"),exe); return; }
            }
        });
    }
    // Inspect shallow installation folders only. Watch filenames, never content writes or the whole drive.
    internal void LooseLibrary(string root,int depth) {
        if (!RealFolder(root)) {Watch(root,"*");return;}
        Watch(root,"*");
        foreach(string dir in Directory.GetDirectories(root)) Try(delegate {
            if(!RealFolder(dir) || Path.GetFileName(dir).StartsWith(".")) return;
            Watch(dir,"*");
            foreach(string exe in Directory.GetFiles(dir,"*.exe")) {
                string name=Path.GetFileNameWithoutExtension(exe);
                if(Utility(exe)) continue;
                bool unity=File.Exists(Path.Combine(dir,"UnityPlayer.dll")) && Directory.Exists(Path.Combine(dir,name+"_Data"));
                bool godot=File.Exists(Path.ChangeExtension(exe,".pck")) && System.Diagnostics.FileVersionInfo.GetVersionInfo(exe).FileDescription!=null &&
                    (System.Diagnostics.FileVersionInfo.GetVersionInfo(exe).ProductName??"").IndexOf("Godot",StringComparison.OrdinalIgnoreCase)>=0;
                bool unreal=Directory.Exists(Path.Combine(dir,"Engine")) && Directory.GetDirectories(dir).Any(sub=>
                    RealFolder(sub) && Directory.Exists(Path.Combine(sub,@"Binaries\Win64")) &&
                    Directory.GetFiles(Path.Combine(sub,@"Binaries\Win64"),"*Shipping.exe").Length>0);
                if(unity || godot || (unreal && Directory.GetDirectories(dir).Any(sub=>GameArtwork.Normalize(Path.GetFileName(sub))==GameArtwork.Normalize(name) || GameArtwork.Normalize(Path.GetFileName(dir))==GameArtwork.Normalize(name)))) {AddExternal("loose:"+exe,name,exe);return;}
            }
            if(depth>1) LooseLibrary(dir,depth-1);
        });
    }
    internal void Gog(string root,string gameId) {
        if(!Directory.Exists(root) || !Regex.IsMatch(gameId,@"^\d+$")) return;
        string manifest=Path.Combine(root,"goggame-"+gameId+".info");
        Watch(root,Path.GetFileName(manifest));
        if(!File.Exists(manifest)) return;
        var item=new JavaScriptSerializer().Deserialize<Dictionary<string,object>>(File.ReadAllText(manifest));
        object tasks;
        if(item==null || Text(item,"gameId")!=gameId || !item.TryGetValue("playTasks",out tasks) || !(tasks is System.Collections.IList)) return;
        foreach(var task in ((System.Collections.IList)tasks).OfType<Dictionary<string,object>>()) {
            object primary;
            if(!task.TryGetValue("isPrimary",out primary) || !Object.Equals(primary,true) || !String.Equals(Text(task,"type"),"File",StringComparison.OrdinalIgnoreCase)) continue;
            string exe=Child(root,Text(task,"path"));
            if(!File.Exists(exe) || !String.Equals(Path.GetExtension(exe),".exe",StringComparison.OrdinalIgnoreCase) || Utility(exe)) continue;
            string icon=Path.ChangeExtension(manifest,".ico");
            Watch(root,Path.GetFileName(icon));
            Games["gog-"+gameId]=new InstalledGame {Title=Text(item,"name"),Target=exe,Arguments=Text(task,"arguments"),Proof=exe,Icon=File.Exists(icon)?icon:exe,Manifest=manifest};
            break;
        }
    }
    void RegisteredGames() {
        foreach(var view in new[]{RegistryView.Registry32,RegistryView.Registry64}) Try(delegate {
            using(var hive=RegistryKey.OpenBaseKey(RegistryHive.LocalMachine,view))
            using(var games=hive.OpenSubKey(@"SOFTWARE\GOG.com\Games")) {
                if(games==null) return;
                foreach(string id in games.GetSubKeyNames()) Try(delegate {
                    using(var game=games.OpenSubKey(id)) {
                        if(game!=null) Gog(game.GetValue("path") as string,id);
                    }
                });
            }
        });
    }
    internal static GameScan Read() {
        var scan = new GameScan();
        scan.RegisteredGames();
        string steam = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),"Steam");
        scan.Try(delegate { steam = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam","SteamPath",steam) as string ?? steam; });
        scan.Try(delegate { scan.Steam(Path.GetFullPath(steam)); });
        scan.Try(delegate { scan.Epic(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),@"Epic\EpicGamesLauncher\Data\Manifests")); });
        foreach (var drive in DriveInfo.GetDrives()) {
            scan.Try(delegate { if (drive.DriveType == DriveType.Fixed && drive.IsReady) scan.Xbox(drive.RootDirectory.FullName); });
        }
        scan.Try(delegate { scan.Riot(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),"Riot Games")); });
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        var commonDesktop = Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory);
        var data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"GameDock");
        foreach (var root in new[]{desktop,commonDesktop}) scan.Try(delegate { scan.Shortcuts(root,false); });
        foreach (var special in new[]{Environment.SpecialFolder.Programs,Environment.SpecialFolder.CommonPrograms}) {
            var riot = Path.Combine(Environment.GetFolderPath(special),"Riot Games");
            scan.Try(delegate { scan.Shortcuts(riot,false); });
        }
        scan.Try(delegate { scan.Shortcuts(Path.Combine(data,"DesktopDock"),true); });
        scan.Try(delegate {
            var config = Path.Combine(data,"folder.txt");
            if (!File.Exists(config)) return;
            var folder = File.ReadAllText(config).Trim();
            if (!Directory.Exists(folder)) return;
            var chosen = Path.GetFullPath(folder).TrimEnd('\\');
            if (!new[]{desktop,commonDesktop}.Contains(chosen,StringComparer.OrdinalIgnoreCase))
                scan.Shortcuts(chosen,true);
        });
        string roaming=Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string profile=Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        scan.Try(delegate { scan.Anker(Path.Combine(roaming,"net.ankergames.launcher")); });
        string home=Path.Combine(roaming,"PrismLauncher");
        foreach(var programs in new[]{Path.Combine(profile,@"AppData\Local\Programs"),Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)}) {
            scan.Try(delegate { scan.Prism(Path.Combine(programs,@"PrismLauncher\prismlauncher.exe"),home); });
        }
        foreach(var drive in DriveInfo.GetDrives()) scan.Try(delegate {
            if(drive.DriveType==DriveType.Fixed && drive.IsReady) scan.LooseLibrary(Path.Combine(drive.RootDirectory.FullName,"Games"),2);
        });
        scan.Try(delegate { scan.LooseLibrary(Path.Combine(profile,"Games"),2); });
        scan.Try(delegate { scan.LooseLibrary(Path.Combine(profile,"Downloads"),2); });
        scan.Consolidate();
        return scan;
    }
    internal static bool Ready(InstalledGame game) {
        if(!File.Exists(game.Proof) || (game.Manifest!=null && !File.Exists(game.Manifest)) ||
            (Path.IsPathRooted(game.Target) && !File.Exists(game.Target))) return false;
        if(game.Manifest==null || !game.Manifest.EndsWith(".acf",StringComparison.OrdinalIgnoreCase)) return true;
        try {
            uint state;
            return UInt32.TryParse(Field(File.ReadAllText(game.Manifest),"StateFlags"),out state) && (state & 4)!=0 && (state & (1u|2048u))==0;
        } catch(IOException) {return false;} catch(UnauthorizedAccessException) {return false;}
    }
    static string Field(string vdf, string key) {
        return Regex.Match(vdf, "\"" + key + "\"\\s*\"([^\"\\r\\n]*)\"", RegexOptions.IgnoreCase).Groups[1].Value.Replace(@"\\",@"\");
    }
    internal void Steam(string root) {
        var libraries = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { root };
        string apps = Path.Combine(root,"steamapps");
        Watch(apps,"libraryfolders.vdf");
        string paths = Path.Combine(apps,"libraryfolders.vdf");
        if (File.Exists(paths)) {
            foreach (Match m in Regex.Matches(File.ReadAllText(paths), "\"path\"\\s*\"([^\"\\r\\n]+)\""))
                libraries.Add(m.Groups[1].Value.Replace(@"\\",@"\"));
        }
        var candidates = new Dictionary<uint,string[]>();
        foreach (string library in libraries) Try(delegate {
            string manifests = Path.Combine(library,"steamapps");
            Watch(manifests,"appmanifest_*.acf");
            if (!Directory.Exists(manifests)) return;
            foreach (string file in Directory.GetFiles(manifests,"appmanifest_*.acf")) Try(delegate {
                string data = File.ReadAllText(file);
                uint id, state;
                string name = Field(data,"name");
                string dir = Child(Path.Combine(manifests,"common"),Field(data,"installdir"));
                if (name.Length > 0 && UInt32.TryParse(Field(data,"appid"),out id) &&
                    UInt32.TryParse(Field(data,"StateFlags"),out state) && (state & 4) != 0 && (state & (1u|2048u))==0 && Directory.Exists(dir))
                    candidates[id] = new[]{name,dir};
            });
        });
        string cache = Path.Combine(root,"appcache");
        Watch(cache,"appinfo.vdf");
        string info = Path.Combine(cache,"appinfo.vdf");
        if (candidates.Count == 0 || !File.Exists(info)) return;
        using (var stream = File.Open(info,FileMode.Open,FileAccess.Read,FileShare.ReadWrite | FileShare.Delete))
        using (var reader = new BinaryReader(stream)) {
            uint version = reader.ReadUInt32();
            if (version != 0x07564429 && version != 0x07564428 && version != 0x07564427) return;
            reader.ReadUInt32();
            string[] keys = null;
            if (version == 0x07564429) {
                long offset = reader.ReadInt64();
                if (offset < 16 || offset > stream.Length-4) return;
                stream.Position = offset;
                int count = reader.ReadInt32();
                if (count < 0 || count > 1000000) return;
                keys = new string[count];
                for (int i=0;i<count;i++) keys[i] = CString(reader,stream.Length);
                stream.Position = 16;
            }
            while (stream.Position+8 <= stream.Length && candidates.Count > 0) {
                uint id = reader.ReadUInt32();
                if (id == 0) break;
                long length = reader.ReadUInt32(), end = stream.Position + length;
                int header = version == 0x07564427 ? 40 : 60;
                if (length < header || end > stream.Length) break;
                string[] game;
                if (candidates.TryGetValue(id,out game)) {
                    candidates.Remove(id);
                    stream.Position += header;
                    var launchFiles = new List<string>();
                    var common = Common(reader,end,keys,launchFiles);
                    string proof=launchFiles.Where(p=>p.EndsWith(".exe",StringComparison.OrdinalIgnoreCase) && !Utility(p))
                        .Select(p=>Child(game[1],p.Replace('/','\\'))).FirstOrDefault(File.Exists);
                    string type, icon;
                    if (proof!=null && common.TryGetValue("type",out type) && String.Equals(type,"game",StringComparison.OrdinalIgnoreCase)) {
                        string image = null;
                        if (common.TryGetValue("clienticon",out icon) && Regex.IsMatch(icon,"^[a-f0-9]{40}$",RegexOptions.IgnoreCase))
                            image = Path.Combine(root,"steam","games",icon+".ico");
                        Games["steam-"+id] = new InstalledGame { Title=game[0],Proof=proof,Manifest=Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(game[1])),"appmanifest_"+id+".acf"),Target="steam://rungameid/"+id,Icon=File.Exists(image)?image:null };
                    }
                }
                stream.Position = end;
            }
        }
    }
    static string CString(BinaryReader reader,long end) {
        var bytes = new List<byte>();
        while (reader.BaseStream.Position < end && bytes.Count < 1048576) {
            byte b=reader.ReadByte(); if(b==0) return Encoding.UTF8.GetString(bytes.ToArray()); bytes.Add(b);
        }
        throw new InvalidDataException();
    }
    static Dictionary<string,string> Common(BinaryReader r,long end,string[] keys,List<string> launches) {
        var data = new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
        var path = new List<string>();
        while(r.BaseStream.Position < end) {
            int kind=r.ReadByte();
            if(kind==8) {
                if(path.Count==0) break;

                path.RemoveAt(path.Count-1); continue;
            }
            string key;
            if(keys==null) key=CString(r,end);
            else { uint index=r.ReadUInt32(); if(index>=keys.Length) break; key=keys[index]; }
            if(kind==0) { if(path.Count>=64) break; path.Add(key); }
            else if(kind==1) {
                string value=CString(r,end);
                if(path.Count==2 && path[0]=="appinfo" && path[1]=="common") data[key]=value;
                if(path.Count==4 && path[1]=="config" && path[2]=="launch" && key=="executable") launches.Add(value);
            } else if(kind==2 || kind==3 || kind==4 || kind==6) r.BaseStream.Position+=4;
            else if(kind==7 || kind==10) r.BaseStream.Position+=8;
            else break;
        }
        return data;
    }
    static string Text(Dictionary<string,object> fields,string key) {
        object value; return fields.TryGetValue(key,out value) ? value as string ?? "" : "";
    }
    internal void Epic(string directory) {
        Watch(directory,"*.item");
        if(!Directory.Exists(directory)) return;
        foreach(string file in Directory.GetFiles(directory,"*.item")) Try(delegate {
            var item = new JavaScriptSerializer().Deserialize<Dictionary<string,object>>(File.ReadAllText(file));
            if(item==null) return;
            object incomplete, raw;
            if(!item.TryGetValue("bIsIncompleteInstall",out incomplete) || !Object.Equals(incomplete,false) ||
                !item.TryGetValue("AppCategories",out raw)) return;
            var categories = raw as System.Collections.IEnumerable;
            if(categories==null || raw is string) return;
            var tags = categories.Cast<object>().OfType<string>().ToArray();
            if(!tags.Contains("games",StringComparer.OrdinalIgnoreCase) || tags.Any(t=>Regex.IsMatch(t,"addon|dlc|digitalextra|editor|plugin|software|engine",RegexOptions.IgnoreCase))) return;
            string app=Text(item,"AppName"), parent=Text(item,"MainGameAppName"), title=Text(item,"DisplayName");
            if(app.Length==0 || title.Length==0 || (parent.Length>0 && parent!=app)) return;
            string root=Text(item,"InstallLocation");
            if(!Directory.Exists(root)) return;
            string exe=Child(root,Text(item,"LaunchExecutable"));
            if(!File.Exists(exe) || !String.Equals(Path.GetExtension(exe),".exe",StringComparison.OrdinalIgnoreCase)) return;
            Games["epic-"+Uri.EscapeDataString(app)] = new InstalledGame {Title=title,Icon=exe,Proof=exe,Manifest=file,Target="com.epicgames.launcher://apps/"+Uri.EscapeDataString(app)+"?action=launch&silent=true"};
        });
    }
    internal void Xbox(string drive) {
        Watch(drive,".GamingRoot");
        string marker=Path.Combine(drive,".GamingRoot");
        if(!File.Exists(marker)) return;
        byte[] bytes=File.ReadAllBytes(marker);
        if(bytes.Length<10 || Encoding.ASCII.GetString(bytes,0,4)!="RGBX") return;
        foreach (var location in Encoding.Unicode.GetString(bytes,8,bytes.Length-8).Split(new[]{'\0'},StringSplitOptions.RemoveEmptyEntries))
            Try(delegate { XboxLibrary(Child(drive,location.TrimStart('\\'))); });
    }
    void XboxLibrary(string root) {
        if(root==null) return;
        Watch(root,"*");
        if(!Directory.Exists(root)) return;
        foreach(string dir in Directory.GetDirectories(root)) Try(delegate {
            string content=Path.Combine(dir,"Content");
            Watch(content,"MicrosoftGame.config"); Watch(content,"gamelaunchhelper.exe");
            string config=Path.Combine(content,"MicrosoftGame.config"), helper=Path.Combine(content,"gamelaunchhelper.exe");
            if(!File.Exists(config) || !File.Exists(helper)) return;
            var xml=XDocument.Load(config);
            var identity=xml.Descendants().FirstOrDefault(e=>e.Name.LocalName=="Identity");
            var executable=xml.Descendants().FirstOrDefault(e=>e.Name.LocalName=="Executable");
            if(identity==null || executable==null) return;
            string id=(string)identity.Attribute("Name"), exe=Child(content,(string)executable.Attribute("Name"));
            if(String.IsNullOrEmpty(id) || exe==null) return;
            if (!File.Exists(exe)) { Watch(Path.GetDirectoryName(exe),Path.GetFileName(exe)); return; }
            var visual=xml.Descendants().FirstOrDefault(e=>e.Name.LocalName=="ShellVisuals");
            string title=visual==null ? null : (string)visual.Attribute("DefaultDisplayName");
            Games["xbox-"+id]=new InstalledGame {Title=String.IsNullOrEmpty(title)||title.StartsWith("ms-resource:",StringComparison.OrdinalIgnoreCase) ? Path.GetFileName(dir):title,Icon=exe,Target=helper,Proof=exe,Manifest=config};
        });
    }
}
