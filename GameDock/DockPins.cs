using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Runtime.InteropServices;

// Explicit additions are independent of scanner manifests and the selected manual folder.
internal sealed class DockPins {
    readonly string directory;
    readonly Dictionary<string,ManualGame> items=new Dictionary<string,ManualGame>(StringComparer.OrdinalIgnoreCase);
    bool loaded;
    internal DockPins(string directory) { this.directory=directory; }
    internal static bool IsPin(string id) {return id!=null && id.StartsWith("pin:",StringComparison.OrdinalIgnoreCase);}
    internal void Invalidate() {loaded=false;}

    internal string Add(string source) {
        if(!File.Exists(source) && !Directory.Exists(source)) throw new FileNotFoundException("Item not found.",source);
        Directory.CreateDirectory(directory);
        string name=GameDock.PlaceEntry(source,directory,true);
        Invalidate();
        return "pin:"+name;
    }
    internal string FileFor(string id) {
        EnsureLoaded();
        return items.ContainsKey(id) ? Path.Combine(directory,id.Substring(4)) : null;
    }
    void EnsureLoaded() {
        if(loaded) return;
        items.Clear();
        if(!Directory.Exists(directory)) { loaded=true; return; }
        object shell=null;
        try {
            foreach(string file in Directory.GetFiles(directory)) {
                string ext=Path.GetExtension(file);
                bool link=ext.Equals(".lnk",StringComparison.OrdinalIgnoreCase);
                if(!link && !ext.Equals(".url",StringComparison.OrdinalIgnoreCase)) continue;
                if(link && shell==null) {
                    try {shell=Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell",true));}
                    catch(COMException) {}
                }
                items["pin:"+Path.GetFileName(file)]=ManualGame.Read(file,shell);
            }
            loaded=true;
        } catch(IOException) {} catch(UnauthorizedAccessException) {}
        finally { if(shell!=null) Marshal.FinalReleaseComObject(shell); }
    }
    internal string[] Entries(GameScan scan,string[] order,string[] hidden) {
        EnsureLoaded();
        var comparer=StringComparer.OrdinalIgnoreCase;
        var excluded=new HashSet<string>(hidden.Select(scan.Resolve),comparer);
        var ids=new List<string>();
        var replacements=new Dictionary<string,string>(comparer);
        foreach(var pair in scan.Games.OrderBy(g=>g.Value.Title,StringComparer.CurrentCultureIgnoreCase)) {
            var game=pair.Value;
            string pin=items.Where(p=>{
                var item=p.Value;
                if(item.SteamId!=null && comparer.Equals(pair.Key,"steam-"+item.SteamId)) return true;
                if(comparer.Equals(item.Target,game.Target) && String.Equals(item.Arguments??"",game.Arguments??"",StringComparison.Ordinal)) return true;
                return String.IsNullOrEmpty(item.Arguments) && comparer.Equals(item.Target,game.Proof);
            }).Select(p=>p.Key).FirstOrDefault();
            if(pin!=null) replacements[pair.Key]=pin;
            else if(!excluded.Contains(pair.Key) && !excluded.Contains("title:"+GameArtwork.Normalize(game.Title))) ids.Add(pair.Key);
        }
        ids.AddRange(items.Keys.Where(id=>!excluded.Contains(id)).OrderBy(id=>id,StringComparer.CurrentCultureIgnoreCase));
        var saved=new HashSet<string>(order,comparer);
        var ranked=order.Select(scan.Resolve).Select(id=>replacements.ContainsKey(id) && !saved.Contains(replacements[id])
            ? replacements[id] : id).Intersect(ids,comparer);
        return ranked.Concat(ids.Except(ranked,comparer)).ToArray();
    }
}
