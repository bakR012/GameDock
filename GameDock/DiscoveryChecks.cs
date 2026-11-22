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
        File.Delete(scan.Games.Values.First().Proof);
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
    static int Main() {
        try {
            RiotAndShortcuts(); Minecraft(); ExternalGames(); StoreManifests(); Steam();
            Check(GameArtwork.LookupTitle("TeamfightTactics")=="Teamfight Tactics" &&
                GameArtwork.Normalize("Sky Walk™ 2")==GameArtwork.Normalize("SkyWalk2"),"artwork title matching");
            return 0;
        } catch(Exception e) {Console.Error.WriteLine(e);return 1;}
        finally {if(Directory.Exists(root)) Directory.Delete(root,true);}
    }
}
