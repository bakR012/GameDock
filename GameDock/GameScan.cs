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
    internal string Title, Target, Icon, Arguments;
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
        if (String.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative)) return null;
        string prefix = Path.GetFullPath(root).TrimEnd('\\') + "\\";
        string path = Path.GetFullPath(Path.Combine(prefix,relative));
        return path.StartsWith(prefix,StringComparison.OrdinalIgnoreCase) ? path : null;
    }
    void Riot(string root) {
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
            if (parts.Length!=2 || !products.TryGetValue(parts[0],out title) || (parts[1]!="live" && parts[1]!="pbe")) return;
            string manifest = name+".product_settings.yaml";
            Watch(dir,manifest);
            string file = Path.Combine(dir,manifest);
            if (!File.Exists(file)) return;
            var line = File.ReadLines(file).FirstOrDefault(l=>l.StartsWith("product_install_full_path:",StringComparison.Ordinal));
            if (line==null) return;
            string location = line.Substring(line.IndexOf(':')+1).Trim().Trim('"');
            if (!Directory.Exists(location) || !Directory.EnumerateFileSystemEntries(location).Any()) return;
            string suffix = parts[1]=="live" ? "" : "-pbe";
            Games["riot-"+parts[0]+suffix] = new InstalledGame {Title=title+(suffix==""?"":" (PBE)"),Target=client,Icon=client,Arguments="--launch-product="+parts[0]+" --launch-patchline="+parts[1]};
        });
    }
    internal void Shortcuts(string directory, bool curated) {
        if (String.IsNullOrWhiteSpace(directory)) return;
        Watch(directory,"*.lnk");
        Watch(directory,"*.url");
        if (!Directory.Exists(directory)) return;
        dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell"));
        try {
            foreach (var file in Directory.EnumerateFiles(directory)) Try(delegate {
                string ext = Path.GetExtension(file).ToLowerInvariant();
                string title = Path.GetFileNameWithoutExtension(file);
                string target = file, args = "", id = null;
                if (ext == ".lnk") {
                    dynamic link = shell.CreateShortcut(file);
                    try { target = link.TargetPath; args = link.Arguments ?? ""; }
                    finally { System.Runtime.InteropServices.Marshal.FinalReleaseComObject(link); }
                } else if (ext == ".url") {
                    var line = File.ReadLines(file).FirstOrDefault(x=>x.StartsWith("URL=",StringComparison.OrdinalIgnoreCase));
                    if (line == null) return;
                    target = line.Substring(4).Trim();
                    var steam = Regex.Match(target,@"^steam://(?:run|rungameid)/(\d+)/?$", RegexOptions.IgnoreCase);
                    if (steam.Success) id = "steam-"+steam.Groups[1].Value;
                    else if (!Regex.IsMatch(target,@"^(?:uplay://launch/\d+|battlenet://[a-z0-9]+|com\.epicgames\.launcher://apps/[^\s]+)(?:[/\?][^\s]*)?$",RegexOptions.IgnoreCase)) return;
                    else id = target;
                    // A Steam URI can also launch utilities. Only trust a curated entry or a verified game.
                    if (steam.Success && !curated && !Games.ContainsKey(id)) return;
                } else if (ext != ".exe" || !curated) return;

                if (ext != ".url") {
                    if (!File.Exists(target) || !String.Equals(Path.GetExtension(target),".exe",StringComparison.OrdinalIgnoreCase)) return;
                    string exe = Path.GetFileNameWithoutExtension(target);
                    if (String.Equals(exe,"RiotClientServices",StringComparison.OrdinalIgnoreCase)) {
                        var product = Regex.Match(args,@"(?:^|\s)--launch-product=(league_of_legends|teamfighttactics|valorant|bacon)(?:\s|$)",RegexOptions.IgnoreCase);
                        if (!product.Success) return;
                        id = "riot-"+product.Groups[1].Value.ToLowerInvariant()+(Regex.IsMatch(args,@"--launch-patchline=pbe(?:\s|$)",RegexOptions.IgnoreCase)?"-pbe":"");
                    } else {
                        if (Regex.IsMatch(exe,@"(?:unins|crashreport|unitycrash|setup|installer|updater|redist)",RegexOptions.IgnoreCase)) return;
                        string parent = Path.GetDirectoryName(target);
                        if (!curated && !(Directory.Exists(Path.Combine(parent,exe+"_Data")) && File.Exists(Path.Combine(parent,"UnityPlayer.dll"))) &&
                            !Regex.IsMatch(target,@"\\Binaries\\Win(?:32|64)\\[^\\]+-Shipping\.exe$",RegexOptions.IgnoreCase)) return;
                        id = target+" "+args;
                    }
                }
                if (id.StartsWith("riot-") && Games.ContainsKey(id)) Games[id].Icon=file;
                if (Games.ContainsKey(id) || Games.Values.Any(g=>String.Equals(g.Title,title,StringComparison.OrdinalIgnoreCase))) return;
                Games[id] = new InstalledGame {Title=title, Icon=file, Target=file};
            });
        } finally { System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell); }
    }
    internal static GameScan Read() {
        var scan = new GameScan();
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
        return scan;
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
                    UInt32.TryParse(Field(data,"StateFlags"),out state) && (state & 4) != 0 && Directory.Exists(dir))
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
                    var common = Common(reader,end,keys);
                    string type, icon;
                    if (common.TryGetValue("type",out type) && String.Equals(type,"game",StringComparison.OrdinalIgnoreCase)) {
                        string image = null;
                        if (common.TryGetValue("clienticon",out icon) && Regex.IsMatch(icon,"^[a-f0-9]{40}$",RegexOptions.IgnoreCase))
                            image = Path.Combine(root,"steam","games",icon+".ico");
                        Games["steam-"+id] = new InstalledGame { Title=game[0],Target="steam://rungameid/"+id,Icon=File.Exists(image)?image:null };
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
    static Dictionary<string,string> Common(BinaryReader r,long end,string[] keys) {
        var data = new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
        var path = new List<string>();
        while(r.BaseStream.Position < end) {
            int kind=r.ReadByte();
            if(kind==8) {
                if(path.Count==0) break;
                if(path.Count==2 && path[1]=="common") break;
                path.RemoveAt(path.Count-1); continue;
            }
            string key;
            if(keys==null) key=CString(r,end);
            else { uint index=r.ReadUInt32(); if(index>=keys.Length) break; key=keys[index]; }
            if(kind==0) { if(path.Count>=64) break; path.Add(key); }
            else if(kind==1) {
                string value=CString(r,end);
                if(path.Count==2 && path[0]=="appinfo" && path[1]=="common") data[key]=value;
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
            Games["epic-"+Uri.EscapeDataString(app)] = new InstalledGame {Title=title,Icon=exe,Target="com.epicgames.launcher://apps/"+Uri.EscapeDataString(app)+"?action=launch&silent=true"};
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
            Games["xbox-"+id]=new InstalledGame {Title=String.IsNullOrEmpty(title)||title.StartsWith("ms-resource:",StringComparison.OrdinalIgnoreCase) ? Path.GetFileName(dir):title,Icon=exe,Target=helper};
        });
    }
}
