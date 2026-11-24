using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;

// All manifests, game payloads and shortcuts in these release checks are temporary fixtures.
internal static class DiscoveryChecks {
    static readonly string root=Path.Combine(Path.GetTempPath(),"gd-"+Guid.NewGuid().ToString("N").Substring(0,12));
    static readonly JavaScriptSerializer json=new JavaScriptSerializer();
    static string Write(string relative,string contents="") {
        string file=Path.Combine(root,relative);
        Directory.CreateDirectory(Path.GetDirectoryName(file));
        File.WriteAllText(file,contents);
        return file;
    }
    static void Check(bool result,string name) {
        if(!result) throw new InvalidDataException(name);
        Console.WriteLine("PASS "+name);
    }
    static void RiotAndShortcuts() {
        var manifestRoot=Path.Combine(root,"m");
        var client=Write(@"launcher\RiotClientServices.exe");
        Write(@"m\RiotClientInstalls.json",json.Serialize(new {rc_default=client}));
        foreach(string channel in new[]{"live","pbe"}) {
            string product="league_of_legends."+channel;
            var location=Path.Combine(root,"custom drive",channel);
            Write(Path.Combine(location,@"Game\League of Legends.exe"));
            Write(Path.Combine(manifestRoot,"Metadata",product,product+".product_settings.yaml"),"product_install_full_path: \""+location+"\"");
        }
        var links=Path.Combine(root,"shortcuts");
        Write(@"shortcuts\Removed.url","[InternetShortcut]\r\nURL=steam://rungameid/123");
        Write(@"shortcuts\Epic.url","[InternetShortcut]\r\nURL=com.epicgames.launcher://apps/Old?action=launch");
        Write(@"shortcuts\RiotClientServices.exe");
        Write(@"shortcuts\OfficeTool.exe");
        var scan=new GameScan();
        scan.Riot(manifestRoot); scan.Shortcuts(links,true); scan.Consolidate();
        Check(scan.Games.Keys.SequenceEqual(new[]{"riot-league_of_legends"}),"live Riot game only, stale shortcuts and utilities ignored");
        var league=scan.Games["riot-league_of_legends"];
        Check(league.Icon==league.Proof && league.Icon!=client,"League uses its own executable when no product icon exists");
        string leagueIcon=Write(@"m\Metadata\league_of_legends.live\league_of_legends.live.ico");
        string valorantRoot=Path.Combine(root,"riot fixtures","valorant");
        Write(Path.Combine(valorantRoot,@"ShooterGame\Binaries\Win64\VALORANT-Win64-Shipping.exe"));
        Write(@"m\Metadata\valorant.live\valorant.live.product_settings.yaml","product_install_full_path: \""+valorantRoot+"\"");
        string valorantIcon=Write(@"m\Metadata\valorant.live\valorant.live.ico");
        scan.Riot(manifestRoot);
        Check(scan.Games["riot-valorant"].Icon==valorantIcon && scan.Games["riot-league_of_legends"].Icon==leagueIcon,"Riot product icons stay distinct on a custom install drive");
        Check(GameArtwork.IsGameIconFile(scan.Games["riot-valorant"].Proof,"VALORANT",true) &&
            !GameArtwork.IsGameIconFile(client,"VALORANT",true),"verified game executable can supply an icon but the shared client cannot");
        File.Delete(scan.Games["riot-valorant"].Proof);
        File.Delete(league.Proof);
        Write(@"custom drive\live\Logs\residual.log");
        scan=new GameScan(); scan.Riot(manifestRoot); scan.Consolidate();
        Check(scan.Games.Count==0,"uninstalled Riot game with leftover folder");
    }
    static void Minecraft() {
        string exe=Write(@"portable\prismlauncher.exe"), home=Path.Combine(root,"prism");
        var scan=new GameScan();
        scan.Prism(exe,home);
        Check(scan.Games.Count==0,"empty launcher");
        foreach(string id in new[]{"pack1","pack2","uninstalled"}) {
            string instance=Path.Combine(home,"instances",id);
            string version=id=="uninstalled" ? "missing" : "1.0";
            Write(Path.Combine(instance,"instance.cfg"),"lastLaunchTime="+(id=="pack1"?"1":"2"));
            Write(Path.Combine(instance,"mmc-pack.json"),json.Serialize(new {components=new[]{new {uid="net.minecraft",version=version}}}));
        }
        Write(Path.Combine(home,@"libraries\com\mojang\minecraft\1.0\minecraft-1.0-client.jar"));
        scan.Prism(exe,home); scan.Consolidate();
        Check(scan.Games.Count==1 && scan.Games.Values.Single().Arguments.EndsWith("\"pack2\""),"one Minecraft tile launching an installed profile");
        var game=scan.Games["prism-minecraft"];
        Check(game.LaunchArguments(false)=="--dir \""+home+"\"" && game.LaunchArguments(true)==game.Arguments,
            "Minecraft picker and last played profile use the same installation");
        Check(new InstalledGame {Arguments="--play"}.LaunchArguments(false)=="--play","Minecraft preference leaves other games alone");
    }
    static void ExternalGames() {
        var library=Path.Combine(root,"gog");
        var exe=Write(Path.Combine(library,"Game.exe"));
        Write(Path.Combine(library,"goggame-123.info"),json.Serialize(new {gameId="123",name="Sky Walk 2",playTasks=new[]{new {isPrimary=true,type="File",path="Game.exe"}}}));
        var scan=new GameScan(); scan.Gog(library,"123");
        var other=Write(@"standalone\SkyWalk2.exe");
        scan.Games["loose"]=new InstalledGame {Title="SkyWalk2",Proof=other,Target=other};
        scan.Games["sequel"]=new InstalledGame {Title="Sky Walk 3",Proof=Write(@"third\Game.exe")};
        scan.Consolidate();
        Check(scan.Games.Count==2 && scan.Resolve("loose")=="gog-123","spaced aliases merge and sequels remain distinct");
        File.Delete(exe);
        scan=new GameScan(); scan.Gog(library,"123");
        Check(scan.Games.Count==0,"leftover GOG manifest");
    }
    static void StoreManifests() {
        var manifests=Path.Combine(root,"egs");
        var install=Path.Combine(root,"elsewhere");
        Write(Path.Combine(install,"play.exe"));
        foreach(var title in new[]{"Retail","Missing","Addon","Incomplete"}) {
            Write(Path.Combine(manifests,title+".item"),json.Serialize(new {
                DisplayName=title,AppName=title,InstallLocation=install,LaunchExecutable=title=="Missing" ? "old.exe" : "play.exe",
                bIsIncompleteInstall=title=="Incomplete",AppCategories=title=="Addon" ? new[]{"games","dlc"} : new[]{"games"}
            }));
        }
        var scan=new GameScan();scan.Epic(manifests);scan.Consolidate();
        Check(scan.Games.Keys.SequenceEqual(new[]{"epic-Retail"}),"Epic validates the installed base game");
        var drive=Path.Combine(root,"volume");
        var marker=Write(Path.Combine(drive,".GamingRoot"));
        File.WriteAllBytes(marker,Encoding.ASCII.GetBytes("RGBX\0\0\0\0").Concat(Encoding.Unicode.GetBytes("My Library\0")).ToArray());
        var game=Path.Combine(drive,@"My Library\game\Content");
        Write(Path.Combine(game,"gamelaunchhelper.exe"));
        Write(Path.Combine(game,"MicrosoftGame.config"),"<Game><Identity Name='test'/><Executable Name='play.exe'/><ShellVisuals DefaultDisplayName='Retail'/></Game>");
        scan=new GameScan();scan.Xbox(drive);
        Check(scan.Games.Count==0,"Xbox helper alone is insufficient");
        Write(Path.Combine(game,"play.exe"));scan.Xbox(drive);scan.Consolidate();
        Check(scan.Games.ContainsKey("xbox-test"),"Xbox on custom volume and library");
    }
    static void Key(BinaryWriter w,byte type,string name) {
        w.Write(type);w.Write(Encoding.UTF8.GetBytes(name));w.Write((byte)0);
    }
    static void Steam() {
        string lib=Path.Combine(root,"OtherSteam");
        string exe=Write(Path.Combine(lib,@"steamapps\common\Space\bin\play.exe"));
        Write(Path.Combine(lib,@"steamapps\appmanifest_42.acf"),"\"appid\" \"42\" \"name\" \"Space\" \"StateFlags\" \"4\" \"installdir\" \"Space\"");
        var file=Write(Path.Combine(lib,@"appcache\appinfo.vdf"));
        using(var data=new MemoryStream()) using(var p=new BinaryWriter(data)) {
            Key(p,0,"appinfo");Key(p,0,"common");Key(p,1,"type");p.Write(Encoding.ASCII.GetBytes("Game\0"));p.Write((byte)8);
            Key(p,0,"config");Key(p,0,"launch");Key(p,0,"0");Key(p,1,"executable");p.Write(Encoding.ASCII.GetBytes("bin/play.exe\0"));
            for(int i=0;i<5;i++) p.Write((byte)8);
            using(var w=new BinaryWriter(File.Create(file))) {
                w.Write(0x07564428u);w.Write(0);w.Write(42);w.Write((int)data.Length+60);
                w.Write(new byte[60]);w.Write(data.ToArray());w.Write(0);
            }
        }
        var scan=new GameScan();scan.Steam(lib);scan.Consolidate();
        Check(scan.Games.ContainsKey("steam-42") && File.Exists(scan.Games["steam-42"].Manifest),"Steam launch binary and manifest from custom library");
        var game=scan.Games["steam-42"];
        Check(GameScan.Ready(game),"installed Steam game can launch");
        string original=File.ReadAllText(game.Manifest);
        File.WriteAllText(game.Manifest,original.Replace("\"4\"","\"2048\""));
        Check(!GameScan.Ready(game),"uninstall started after discovery blocks launch");
        File.WriteAllText(game.Manifest,original);
        File.Delete(exe);scan=new GameScan();scan.Steam(lib);
        Check(scan.Games.Count==0,"stale Steam manifest and empty game directory");
    }
    static void MakeLink(dynamic shell,string path,string target,string args,string icon) {
        dynamic link=shell.CreateShortcut(path);
        try {link.TargetPath=target;link.Arguments=args;link.IconLocation=icon+",0";link.Save();}
        finally {System.Runtime.InteropServices.Marshal.FinalReleaseComObject(link);}
    }
    static byte[] Picture(int width,int height) {
        using(var image=new System.Drawing.Bitmap(width,height))
        using(var memory=new MemoryStream()) {
            image.Save(memory,System.Drawing.Imaging.ImageFormat.Png);
            return memory.ToArray();
        }
    }
    static void Icons() {
        var provider=Write(@"provider\Runner.exe");
        var shared=Write(@"provider\Badge.ico");
        var prism=Write(@"provider\prismlauncher.exe");
        object shell=Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell"));
        try {
            foreach(string title in new[]{"LANESPLIT","A Plague Tale Requiem"}) {
                string link=Path.Combine(root,title+".lnk");
                MakeLink(shell,link,provider,"--game 1",shared);
                var game=ManualGame.Read(link,shell);
                Check(!game.LauncherOnly && game.Icon==null && game.Title==title,"ignore shared launcher badge for "+title);
            }
            var linkPath=Path.Combine(root,"Prism Launcher.lnk");
            MakeLink(shell,linkPath,prism,"",prism);
            Check(ManualGame.Read(linkPath,shell).LauncherOnly,"standalone Prism shortcut hidden in manual mode");
            MakeLink(shell,linkPath,prism,"--launch pack",prism);
            var profile=ManualGame.Read(linkPath,shell);
            Check(!profile.LauncherOnly && profile.Title=="Minecraft" && profile.Icon==null,"Prism game shortcut keeps its launch action and game identity");
            var url=Write("Custom.url","[InternetShortcut]\r\nURL=steam://rungameid/3278310\r\nIconFile="+shared);
            var steam=ManualGame.Read(url,shell);
            Check(steam.SteamId=="3278310" && steam.Icon==null,"URL ignores shared launcher icon");
            string custom=Path.Combine(root,"my choice.ico");
            using(var file=File.Create(custom)) System.Drawing.SystemIcons.Information.Save(file);
            MakeLink(shell,linkPath,provider,"--game 2",custom);
            Check(ManualGame.Read(linkPath,shell).Icon==custom,"arbitrarily named custom shortcut icon is preserved");
            File.WriteAllText(url,"[InternetShortcut]\r\nURL=steam://rungameid/3278310\r\nIconFile="+custom);
            Check(ManualGame.Read(url,shell).Icon==custom,"Steam shortcut retains the user's ICO");
            var shortcuts=new GameScan();
            shortcuts.Games.Add("steam-3278310",new InstalledGame {Title="Game",Icon=provider,Proof=provider});
            shortcuts.Shortcuts(root,false);
            Check(shortcuts.Games["steam-3278310"].ShortcutIcon==custom,"automatic mode respects a verified game's desktop icon");
        } finally {System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell);}
        const string icon="https://shared.fastly.steamstatic.com/community_assets/images/apps/1182900/eaebd6a8952539659e28725f90802cd1a4f93d37.jpg";
        const string cover="https://shared.fastly.steamstatic.com/store_item_assets/steam/apps/1182900/capsule_184x69.jpg";
        string rows=json.Serialize(new[]{new {appid="752590",name="A Plague Tale: Innocence",icon="",logo=cover},new {appid="1182900",name="A Plague Tale: Requiem",icon=icon,logo=cover}});
        Check(GameArtwork.MatchSteamIcon(rows,"A Plague Tale Requiem",null)==icon,"select exact edition and Steam icon field");
        Check(GameArtwork.MatchSteamIcon(rows.Replace(icon,cover),"A Plague Tale Requiem",null)==null,"reject store cover even in the icon field");
        Check(GameArtwork.MatchSteamIcon(rows,"A Plague Tale Requiem","3278310")==null,"ignore icon from different Steam app");
        try {GameArtwork.Decode(Picture(184,69));throw new Exception("accepted rectangular artwork");}
        catch(InvalidDataException) {Check(true,"reject rectangular images before scaling");}
        Check(GameArtwork.Decode(Picture(32,32)).PixelWidth==32,"keep original square icon dimensions");
        string archive=Write("client.jar");
        using(var zip=new System.IO.Compression.ZipArchive(File.Create(archive),System.IO.Compression.ZipArchiveMode.Create))
        using(var entry=zip.CreateEntry("assets/minecraft/icons/release/icon_32x32.png").Open()) {
            var bytes=Picture(32,32);entry.Write(bytes,0,bytes.Length);
        }
        Check(GameArtwork.MinecraftIcon(archive).PixelWidth==32,"read the game icon from the Minecraft client");
        var cache=Path.Combine(root,"cache");
        var local=GameArtwork.Decode(Picture(32,32));
        GameArtwork.RememberLocal("VALORANT",null,provider,local,cache).GetAwaiter().GetResult();
        Check(GameArtwork.LastLocal("VALORANT",null,cache).GetAwaiter().GetResult().PixelWidth==32 &&
            GameArtwork.LastLocal("League of Legends",null,cache).GetAwaiter().GetResult()==null,"local icon survives restart without leaking into another game");
        string riotSite=GameArtwork.PublisherSite("LoL");
        var links=GameArtwork.PublisherIcons(riotSite,"<link rel='preload' href='/cover.png'><link href='/league.ico' rel='shortcut icon'><link href='https://example.org/logo.png' rel='icon'>").ToArray();
        Check(links.SequenceEqual(new[]{new Uri(new Uri(riotSite),"/league.ico").AbsoluteUri,new Uri(new Uri(riotSite),"/favicon.ico").AbsoluteUri}) &&
            GameArtwork.PublisherSite("League of Legends PBE")==null,"publisher fallback accepts only game-site icon links and exact editions");
        Check(GameArtwork.ReadOrFetch("mc","Minecraft",null,false,archive,false,cache).PixelWidth==32,"offline local icon lookup");
        using(var zip=new System.IO.Compression.ZipArchive(File.Create(archive),System.IO.Compression.ZipArchiveMode.Create))
        using(var entry=zip.CreateEntry("icons/icon_32x32.png").Open()) {
            var replacement=Picture(48,48);entry.Write(replacement,0,replacement.Length);
        }
        Check(GameArtwork.ReadOrFetch("mc","Minecraft",null,true,archive,false,cache).PixelWidth==48 &&
            GameArtwork.ReadOrFetch("mc","Minecraft",null,false,null,false,cache).PixelWidth==48,"repair replaces cached icon and survives offline restart");
        Check(GameArtwork.ReadIconFile(Write("bad.ico","broken"))==null,"broken Windows icon falls through to automatic lookup");
    }
    static void PersonalPins() {
        string storage=Path.Combine(root,"pin-store");
        var pins=new DockPins(storage);
        var first=Write(@"personal\a\editor.exe");
        var second=Write(@"personal\b\editor.exe");
        var note=Write(@"personal\notes.txt","keep me");
        var folder=Path.GetDirectoryName(note);
        string app=pins.Add(first), other=pins.Add(second), doc=pins.Add(note), dir=pins.Add(folder);
        Check(app==pins.Add(first) && other==pins.Add(second) && app!=other,"repeated app pins reuse shortcuts and same-name apps stay distinct");
        var url=Write(@"personal\Play.url","[InternetShortcut]\r\nURL=steam://rungameid/314");
        var play=pins.Add(url);
        Check(File.Exists(url) && File.ReadAllText(pins.FileFor(play))==File.ReadAllText(url),"pinning a shortcut copies it and preserves the original");
        File.Delete(url);
        string launcherPath=Write(@"personal\prismlauncher.exe");
        string launcher=pins.Add(launcherPath);
        var empty=new string[0];
        string[] order={launcher,doc,play,dir,other,app};
        pins=new DockPins(storage);
        Check(pins.Entries(new GameScan(),order,empty).SequenceEqual(order),"apps, launchers, documents, folders and copied URLs survive a cold start before detection");
        var scan=new GameScan();
        scan.Games["steam-314"]=new InstalledGame {Title="Game",Target="steam://rungameid/314"};
        scan.Games["other-game"]=new InstalledGame {Title="Another game",Proof=Write(@"fixture\game.exe")};
        Check(pins.Entries(scan,new[]{"steam-314","other-game"}.Concat(order).ToArray(),empty).SequenceEqual(new[]{"other-game"}.Concat(order)),
            "matching detected game does not duplicate or displace an explicitly positioned pin");
        Check(!pins.Entries(scan,order,new[]{play}).Any(id=>id==play || id=="steam-314") &&
            pins.Entries(scan,order,empty).Contains(play),"removal survives rescan and restoration returns the pinned game");
        Check(File.Exists(first) && File.Exists(second) && File.ReadAllText(note)=="keep me" && Directory.Exists(folder),
            "pinning and hiding do not modify original files or directories");
        object shell=Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell"));
        try {
            var link=Path.Combine(folder,"Play profile.lnk");
            MakeLink(shell,link,launcherPath,"--launch pack",null);
            string profile=pins.Add(link);
            var saved=ManualGame.Read(pins.FileFor(profile),shell);
            Check(String.Equals(saved.Target,launcherPath,StringComparison.OrdinalIgnoreCase) && saved.Arguments=="--launch pack","pin preserves a launcher's exact profile arguments");
        } finally {System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell);}
    }
    static void CheckNativeFrames() {
        var trace=new NativeFrames(71);
        var data=System.Runtime.InteropServices.Marshal.AllocHGlobal(16);
        try {
            long hz=System.Diagnostics.Stopwatch.Frequency;
            var r=new NativeFrames.Record {Provider=new Guid("ca11c036-0102-4a2d-a6ad-f03cfed5d3c9"),
                Thread=1,Pid=71,Data=data,Length=16,Flags=0x20};
            for(int i=0;i<=60;i++) {
                r.Stamp=hz+i*hz/60;r.Id=42;
                System.Runtime.InteropServices.Marshal.WriteInt32(data,100);
                System.Runtime.InteropServices.Marshal.WriteInt32(data,4,0);trace.Accept(ref r);
                r.Id=43;System.Runtime.InteropServices.Marshal.WriteInt32(data,0);trace.Accept(ref r);
                r.Stamp++;r.Id=42;System.Runtime.InteropServices.Marshal.WriteInt32(data,4,1);trace.Accept(ref r);
                r.Id=43;trace.Accept(ref r);
            }
            Check(Math.Abs(trace.Rate(2*hz)-60)<0.01,"32-bit DXGI presentation events decode correctly and skip test presents");
            var older=new NativeFrames(71);r.Provider=new Guid("783aca0a-790e-4d7f-8451-aa850511c6b9");r.Flags=0x40;
            for(int i=0;i<=30;i++) {
                r.Stamp=hz+i*hz/30;r.Id=1;System.Runtime.InteropServices.Marshal.WriteInt64(data,780);
                System.Runtime.InteropServices.Marshal.WriteInt32(data,8,0);older.Accept(ref r);
                r.Id=2;System.Runtime.InteropServices.Marshal.WriteInt32(data,0);older.Accept(ref r);
            }
            Check(Math.Abs(older.Rate(2*hz)-30)<0.01,"64-bit Direct3D 9 presents are counted");
        } finally {System.Runtime.InteropServices.Marshal.FreeHGlobal(data);}
    }
    static void FrameRates() {
        var meter=new FrameSamples(42);
        long hz=System.Diagnostics.Stopwatch.Frequency,baseline=hz*10;
        for(int i=0;i<=120;i++) {
            long stamp=baseline+i*hz/120;
            meter.Add(100,stamp);
            if(i%2==0) meter.Add(200,stamp);
        }
        Check(Math.Abs(meter.Rate(baseline+hz)-120)<0.01,"FPS selects a single swap chain at 120 FPS");
        for(int i=1;i<=135;i++) meter.Add(100,baseline+hz+i*hz/30);
        Check(Math.Abs(meter.Rate(baseline+hz*5+hz/2)-30)<0.01,"FPS tracks a change from 120 to 30 FPS");
        Check(meter.Rate(baseline+hz*9)==0,"stale FPS expires");
        var slow=new FrameSamples(7);slow.Add(1,hz);slow.Add(1,hz);slow.Add(1,hz*3);
        Check(slow.Rate(hz*3)==0.5,"slow frames and duplicate timestamps");
        var proof=new FpsLaunch(@"C:\Games\Example\Start.exe","Example",null);
        Check(proof.Matches(@"C:\Games\Example\Bin\Binaries\Win64\Example-Shipping.exe","",0) &&
            !proof.Matches(@"C:\Games\ExampleOther\Binaries\Win64\Other-Shipping.exe","",0),"renderer stays within the launched game directory");
        CheckNativeFrames();
        using(var control=new FpsSession(Guid.NewGuid().ToString("N"),true))
        using(var worker=control.StartWorker(false)) {
            control.TargetGame(0);
            bool idle=System.Threading.SpinWait.SpinUntil(delegate {
                int state;return control.Reading(out state)==0 && state==1;
            },5000);
            control.TargetGame(-1);
            Check(worker.WaitForExit(4500) && idle,"FPS worker connects, acknowledges idle and exits when disabled");
        }
        var overlay=new FpsHud();
        try {
            var handle=new System.Windows.Interop.WindowInteropHelper(overlay).EnsureHandle();
            int style=FpsWindows.Style(handle,-20);
            Check(!overlay.ShowActivated && !overlay.ShowInTaskbar && !overlay.IsHitTestVisible &&
                (style&0x080000a0)==0x080000a0 && (style&0x40000)==0,"FPS overlay passes clicks through and stays out of Alt+Tab");
        } finally {overlay.Close();}
    }
    [STAThread]
    static int Main(string[] args) {
        if(args.Length>0 && args[0]=="--fps-session") {FpsSession.Worker(args);return 0;}
        try {
            if(args.Contains("--live-fps")) {FpsLiveCheck.Run();return 0;}
            if(args.Contains("--replay-fps")) {FpsLiveCheck.Run(true);return 0;}
            if(args.Contains("--online-icons")) {
                foreach(string title in new[]{"VALORANT","League of Legends","Teamfight Tactics","Fortnite","Bloons TD 6","A Plague Tale Requiem","LANESPLIT","Minecraft"}) {
                    var icon=GameArtwork.Get(title,null,true).GetAwaiter().GetResult();
                    Check(icon!=null && icon.PixelWidth==icon.PixelHeight,"live publisher icon for "+title);
                }
                return 0;
            }
            RiotAndShortcuts(); Minecraft(); ExternalGames(); StoreManifests(); Steam(); Icons(); PersonalPins(); FrameRates();
            const string release="{\"tag_name\":\"v1.10.0\",\"draft\":false,\"prerelease\":false}";
            Check(ProjectInfo.PublishedVersion(release)>new Version(1,9,0,0),"update check compares numeric versions");
            foreach(string invalid in new[]{release.Replace("false","true"),release.Replace("v1.10.0","v1.10.0-beta.1"),"{}"}) {
                try {ProjectInfo.PublishedVersion(invalid);throw new Exception("Unexpected release format accepted");}
                catch(InvalidDataException) {}
            }
            Check(true,"only stable published release tags are accepted");
            Check(MinecraftLauncher.Known("Lunar Client")=="Lunar Client" && MinecraftLauncher.Known("Badlion Client")=="Badlion Client" &&
                MinecraftLauncher.Known("TLauncher")=="TLauncher" && MinecraftLauncher.Known("Updater")==null &&
                MinecraftLauncher.SupportsFile("custom.lnk") && MinecraftLauncher.SupportsFile("portable.jar"),"Minecraft launcher discovery and custom selection");
            string[] original={"pin:app.lnk","prism-minecraft","other"}, ranked={"other","prism-minecraft","pin:app.lnk"};
            Check(GameDock.MinecraftEntries(original,ranked,false,id=>id=="prism-minecraft").SequenceEqual(new[]{"other","minecraft:choice","pin:app.lnk"}),
                "changing Minecraft launcher preserves order and replaces detected Minecraft");
            Check(GameDock.MinecraftEntries(original,ranked,true,id=>id=="prism-minecraft").SequenceEqual(new[]{"pin:app.lnk","other"}),
                "hiding configured Minecraft survives a rescan");
            using(var art=typeof(GameDock).Assembly.GetManifestResourceStream("GameDock.Minecraft.png"))
                Check(GameArtwork.Decode(new BinaryReader(art).ReadBytes((int)art.Length)).PixelWidth==32,"Minecraft logo available offline");
            Check(GameArtwork.LookupTitle("TeamfightTactics")=="Teamfight Tactics" &&
                GameArtwork.Normalize("Sky Walk™ 2")==GameArtwork.Normalize("SkyWalk2"),"artwork title matching");
            return 0;
        } catch(Exception e) {Console.Error.WriteLine(e);return 1;}
        finally {if(Directory.Exists(root)) Directory.Delete(root,true);}
    }
}
