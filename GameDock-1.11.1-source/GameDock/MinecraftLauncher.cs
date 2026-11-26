using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Runtime.InteropServices;

internal sealed class MinecraftLauncher {
    internal readonly string Name,Target;
    internal MinecraftLauncher(string name,string target) {Name=name; Target=target;}
    internal const string ShellPrefix=@"shell:AppsFolder\";
    internal bool IsShell {get {return Target.StartsWith(ShellPrefix,StringComparison.OrdinalIgnoreCase);}}
    internal bool IsPrism {get {return GameScan.IsPrism(Target) || GameArtwork.Normalize(Name)=="prismlauncher";}}
    internal bool CanChooseProfile {get {return IsPrism && String.Equals(Path.GetExtension(Target),".exe",StringComparison.OrdinalIgnoreCase);}}
    internal string LastProfileArguments() {
        var local=Path.GetDirectoryName(Target);
        var home=File.Exists(Path.Combine(local,"prismlauncher.cfg")) ? local :
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),"PrismLauncher");
        var scan=new GameScan();
        scan.Prism(Target,home);
        InstalledGame game;
        return scan.Games.TryGetValue("prism-minecraft",out game) && GameScan.Ready(game) ? game.LaunchArguments(true) : "";
    }
    internal static bool SupportsFile(string path) {
        return new[]{".exe",".lnk",".jar"}.Contains(Path.GetExtension(path),StringComparer.OrdinalIgnoreCase);
    }
    internal static string Known(string name) {
        string key=GameArtwork.Normalize(name);
        switch(key) {
            case "minecraftlauncher": return "Minecraft Launcher";
            case "prismlauncher": return "Prism Launcher";
            case "lunarclient": return "Lunar Client";
            case "badlionclient": return "Badlion Client";
            case "tlauncher": return "TLauncher";
            case "modrinthapp": case "modrinth": return "Modrinth App";
            case "atlauncher": return "ATLauncher";
            case "multimc": return "MultiMC";
            case "gdlauncher": case "gdlaunchercarbon": return "GDLauncher";
            case "sklauncher": return "SKlauncher";
            case "curseforge": return "CurseForge";
            case "techniclauncher": return "Technic Launcher";
            case "featherlauncher": return "Feather Launcher";
            default: return null;
        }
    }
    internal static List<MinecraftLauncher> Installed() {
        var found=new Dictionary<string,MinecraftLauncher>(StringComparer.OrdinalIgnoreCase);
        string local=Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        foreach(string root in new[]{local,Path.Combine(local,"Programs"),Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)}) {
            foreach(string relative in new[]{@"PrismLauncher\prismlauncher.exe",@"Lunar Client\Lunar Client.exe",@"Badlion Client\Badlion Client.exe",
                @"Minecraft Launcher\MinecraftLauncher.exe",@".tlauncher\TLauncher.exe",@"Modrinth App\Modrinth App.exe",@"MultiMC\MultiMC.exe",@"ATLauncher\ATLauncher.exe"}) {
                string exe=Path.Combine(root,relative), name=Known(Path.GetFileNameWithoutExtension(exe));
                if(name!=null && File.Exists(exe) && !found.ContainsKey(name)) found.Add(name,new MinecraftLauncher(name,exe));
            }
        }
        // Windows exposes packaged launchers and nonstandard installation paths here.
        dynamic shell=null,folder=null,items=null;
        try {
            shell=Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application",true));
            folder=shell.NameSpace("shell:AppsFolder");
            if(folder!=null) {
                items=folder.Items();
                int count=items.Count;
                for(int i=0;i<count;i++) {
                    dynamic item=items.Item(i);
                    if(item==null) continue;
                    try {
                        string name=Known((string)item.Name);
                        if(name==null || found.ContainsKey(name)) continue;
                        string id=item.ExtendedProperty("System.AppUserModel.ID") as string;
                        string path=item.Path as string;
                        if(!String.IsNullOrEmpty(path) && File.Exists(path) && SupportsFile(path))
                            found[name]=new MinecraftLauncher(name,path);
                        else if(!String.IsNullOrEmpty(id) && id.IndexOf('"')<0)
                            found[name]=new MinecraftLauncher(name,ShellPrefix+id);
                    } catch(COMException) {} finally {Marshal.FinalReleaseComObject(item);}
                }
            }
        } catch(COMException) {} finally {
            if(items!=null) Marshal.FinalReleaseComObject(items);
            if(folder!=null) Marshal.FinalReleaseComObject(folder);
            if(shell!=null) Marshal.FinalReleaseComObject(shell);
        }
        return found.Values.OrderBy(l=>l.Name,StringComparer.CurrentCultureIgnoreCase).ToList();
    }
}
