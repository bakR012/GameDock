using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Media.Imaging;
using System.Text.RegularExpressions;

internal static class GameArtwork {
    static readonly string folder=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"GameDock","GameIcons-v1");
    static readonly SemaphoreSlim workers=new SemaphoreSlim(2);
    static readonly Dictionary<string,Task<BitmapSource>> pending=new Dictionary<string,Task<BitmapSource>>();
    static readonly Dictionary<string,BitmapSource> recent=new Dictionary<string,BitmapSource>();
    static readonly Queue<string> order=new Queue<string>();
    static long reconnectedAt;
    internal static void NetworkRestored() {Interlocked.Exchange(ref reconnectedAt,DateTime.UtcNow.Ticks);}

    static GameArtwork() { ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12; }
    internal static string Normalize(string title) {
        return Regex.Replace((title??"").Replace("™","").Replace("®","").Replace("+"," plus ").Normalize(NormalizationForm.FormKC).ToLowerInvariant(),@"[^\p{L}\p{Nd}]","");
    }
    internal static async Task<BitmapSource> Get(string title,string steamId,bool retry=false,string minecraftJar=null,bool online=true) {
        title=LookupTitle(title);
        if(title.Length==0 || title.Length>180) return null;
        string key=steamId==null ? "title-"+Normalize(title) : "steam-"+steamId;
        string requestKey=key+(retry ? ":refresh" : online ? ":lookup" : ":local");
        Task<BitmapSource> task;
        lock(pending) {
            BitmapSource cached;
            if(!retry && recent.TryGetValue(key,out cached)) return cached;
            if(!pending.TryGetValue(requestKey,out task)) {
                task=Load(key,title,steamId,retry,minecraftJar,online);
                pending.Add(requestKey,task);
            }
        }
        var result=await task.ConfigureAwait(false);
        lock(pending) {
            Task<BitmapSource> current;
            if(pending.TryGetValue(requestKey,out current) && current==task) {
                pending.Remove(requestKey);
                if(result==null) return null;
                if(!recent.ContainsKey(key)) {
                    if(order.Count>=64) recent.Remove(order.Dequeue());
                    order.Enqueue(key);
                }
                recent[key]=result;
            }
        }
        return result;
    }
    static async Task<BitmapSource> Load(string key,string title,string steamId,bool retry,string minecraftJar,bool online) {
        await workers.WaitAsync().ConfigureAwait(false);
        try { return await Task.Run(()=>ReadOrFetch(key,title,steamId,retry,minecraftJar,online)).ConfigureAwait(false); }
        catch(Exception) { return null; }
        finally { workers.Release(); }
    }
    internal static BitmapSource Decode(byte[] data) {
        using(var input=new MemoryStream(data)) {
            var decoder=BitmapDecoder.Create(input,BitmapCreateOptions.DelayCreation,BitmapCacheOption.None);
            var frame=decoder.Frames[0];
            if(frame.PixelWidth<16 || frame.PixelWidth!=frame.PixelHeight || frame.PixelWidth>1024) throw new InvalidDataException("Expected a square game icon.");
            input.Position=0;
            var image=new BitmapImage();
            image.BeginInit(); image.CacheOption=BitmapCacheOption.OnLoad;
            image.DecodePixelWidth=Math.Min(96,frame.PixelWidth); image.StreamSource=input; image.EndInit();
            if(image.PixelHeight<4 || image.PixelHeight>512) throw new InvalidDataException();
            // Detach the thumbnail from the encoded source and its MemoryStream.
            var small=new WriteableBitmap(image);
            small.Freeze(); return small;
        }
    }
    static byte[] Fetch(string url,int limit) {
        var request=(HttpWebRequest)WebRequest.Create(url);
        request.Timeout=5500; request.ReadWriteTimeout=5500; request.AllowAutoRedirect=false;
        request.UserAgent="GameDock/1.7.1 (github.com/bakR012/GameDock)";
        using(var response=(HttpWebResponse)request.GetResponse())
        using(var stream=response.GetResponseStream())
        using(var copy=new MemoryStream()) {
            if(response.ContentLength>limit) throw new InvalidDataException();
            var buf=new byte[8192]; int count;
            while((count=stream.Read(buf,0,buf.Length))>0) {
                if(copy.Length+count>limit) throw new InvalidDataException();
                copy.Write(buf,0,count);
            }
            return copy.ToArray();
        }
    }
    static string Value(Dictionary<string,object> obj,string field) {
        object value; return obj!=null && obj.TryGetValue(field,out value) ? Convert.ToString(value) : "";
    }
    internal static bool IsGameIconFile(string path,string title) {
        if(!File.Exists(path)) return false;
        string ext=Path.GetExtension(path);
        if(String.Equals(ext,".ico",StringComparison.OrdinalIgnoreCase)) return true;
        if(!String.Equals(ext,".exe",StringComparison.OrdinalIgnoreCase) || GameScan.IsLauncher(path)) return false;
        try {
            var info=System.Diagnostics.FileVersionInfo.GetVersionInfo(path);
            string name=Normalize(title);
            return name.Length>0 && (Normalize(info.ProductName)==name || Normalize(info.FileDescription)==name);
        } catch(IOException) {return false;} catch(UnauthorizedAccessException) {return false;}
    }
    internal static BitmapSource ReadIconFile(string path) {
        try {
            var file=new FileInfo(path);
            if(!file.Exists || file.Length>2097152) return null;
            using(var input=File.OpenRead(path)) {
                var decoder=new IconBitmapDecoder(input,BitmapCreateOptions.PreservePixelFormat,BitmapCacheOption.OnLoad);
                var frame=decoder.Frames.Where(f=>f.PixelWidth==f.PixelHeight && f.PixelWidth>=16 && f.PixelWidth<=512)
                    .OrderBy(f=>Math.Abs(f.PixelWidth-32)).ThenByDescending(f=>f.Format.BitsPerPixel).FirstOrDefault();
                if(frame==null) return null;
                var bitmap=new System.Windows.Media.Imaging.FormatConvertedBitmap(frame,System.Windows.Media.PixelFormats.Bgra32,null,0);
                int stride=bitmap.PixelWidth*4;
                var pixels=new byte[stride*bitmap.PixelHeight];
                bitmap.CopyPixels(pixels,stride,0);
                for(int i=3;i<pixels.Length;i+=4) if(pixels[i]>0) {bitmap.Freeze();return bitmap;}
            }
        } catch(IOException) {} catch(UnauthorizedAccessException) {} catch(FormatException) {}
          catch(NotSupportedException) {} catch(ArgumentException) {} catch(System.Runtime.InteropServices.COMException) {}
        return null;
    }
    internal static string LookupTitle(string title) {
        string clean=Regex.Replace((title??"").Trim(),@"(?:\s*[-–(]\s*(?:shortcut|اختصار)\)?|\s+\(\d+\))$","",RegexOptions.IgnoreCase);
        clean=Regex.Replace(clean.Replace('_',' '),@"(?<=[a-z])(?=[A-Z])"," ");
        switch(Normalize(clean)) {
            case "lol": case "leagueoflegends": return "League of Legends";
            case "tft": case "teamfighttactics": return "Teamfight Tactics";
            default: return clean;
        }
    }
    internal static string MatchSteamIcon(string json,string title,string expectedId) {
        var rows=new JavaScriptSerializer().Deserialize<List<Dictionary<string,object>>>(json);
        string match=null;
        foreach(var row in rows ?? new List<Dictionary<string,object>>()) {
            string id=Value(row,"appid");
            if(!Regex.IsMatch(id,@"^\d{1,10}$") || (expectedId!=null ? expectedId!=id : Normalize(Value(row,"name"))!=Normalize(title))) continue;
            Uri uri;
            if(!Uri.TryCreate(Value(row,"icon"),UriKind.Absolute,out uri) || uri.Scheme!="https" ||
               !(uri.Host.EndsWith(".steamstatic.com",StringComparison.OrdinalIgnoreCase) || uri.Host.EndsWith(".akamaihd.net",StringComparison.OrdinalIgnoreCase) && uri.Host.StartsWith("steamcdn-",StringComparison.OrdinalIgnoreCase)) ||
               !Regex.IsMatch(uri.AbsolutePath,@"^/(?:community_assets/images|steamcommunity/public/images)/apps/"+id+@"/[a-fA-F0-9]{40}\.(?:jpg|png|ico)$")) continue;
            if(match!=null && match!=uri.AbsoluteUri) return null;
            match=uri.AbsoluteUri;
        }
        return match;
    }
    static string SearchSteam(string title,string expectedId) {
        string result=SearchIconByName(title,expectedId);
        if(result!=null || expectedId==null || !Regex.IsMatch(expectedId,@"^\d{1,10}$")) return result;
        // A manually renamed Steam shortcut still identifies its game by app ID.
        var metadata=new JavaScriptSerializer().Deserialize<Dictionary<string,object>>(Encoding.UTF8.GetString(
            Fetch("https://store.steampowered.com/api/appdetails?appids="+expectedId+"&filters=basic",262144)));
        object record,data;
        if(metadata==null || !metadata.TryGetValue(expectedId,out record)) return null;
        var app=record as Dictionary<string,object>;
        if(app==null || !app.TryGetValue("data",out data)) return null;
        string official=Value(data as Dictionary<string,object>,"name");
        return official.Length>0 && official.Length<=180 && !String.Equals(official,title,StringComparison.OrdinalIgnoreCase)
            ? SearchIconByName(official,expectedId) : null;
    }
    static string SearchIconByName(string title,string expectedId) {
        byte[] json=Fetch("https://steamcommunity.com/actions/SearchApps/"+Uri.EscapeDataString(title),196608);
        return MatchSteamIcon(Encoding.UTF8.GetString(json),title,expectedId);
    }
    static string MacGameIcon(string title) {
        var url="https://itunes.apple.com/search?country=us&entity=macSoftware&limit=8&term="+Uri.EscapeDataString(title);
        var doc=new JavaScriptSerializer().Deserialize<Dictionary<string,object>>(Encoding.UTF8.GetString(Fetch(url,196608)));
        object results;
        if(doc==null || !doc.TryGetValue("results",out results) || !(results is System.Collections.IList)) return null;
        string match=null;
        foreach(var row in ((System.Collections.IList)results).OfType<Dictionary<string,object>>()) {
            Uri icon;
            if(Value(row,"primaryGenreName")!="Games" || Normalize(Value(row,"trackName"))!=Normalize(title) ||
               !Uri.TryCreate(Value(row,"artworkUrl100"),UriKind.Absolute,out icon) || icon.Scheme!="https" ||
               !icon.Host.EndsWith(".mzstatic.com",StringComparison.OrdinalIgnoreCase)) continue;
            if(match!=null && match!=icon.AbsoluteUri) return null;
            match=icon.AbsoluteUri;
        }
        return match;
    }
    static bool WaitBeforeRetry(string marker) {
        if(!File.Exists(marker)) return false;
        DateTime written=File.GetLastWriteTimeUtc(marker);
        bool temporary=File.ReadAllText(marker)=="offline";
        if(temporary && written.Ticks<Interlocked.Read(ref reconnectedAt)) return false;
        return written > DateTime.UtcNow.AddMinutes(temporary ? -15 : -720);
    }
    internal static BitmapSource MinecraftIcon(string path) {
        using(var stream=File.OpenRead(path))
        using(var jar=new System.IO.Compression.ZipArchive(stream,System.IO.Compression.ZipArchiveMode.Read)) {
            foreach(string name in new[]{"assets/minecraft/icons/release/icon_32x32.png","assets/minecraft/icons/icon_32x32.png","icons/icon_32x32.png"}) {
                var entry=jar.GetEntry(name);
                if(entry==null || entry.Length<1 || entry.Length>131072) continue;
                using(var reader=new BinaryReader(entry.Open())) return Decode(reader.ReadBytes((int)entry.Length));
            }
        }
        return null;
    }
    // Mojang Java Edition asset index 5, icons/icon_32x32.png (content-addressed).
    internal const string MinecraftWindowIcon="af96f55a90eaf11b327f1b5f8834a051027dc506";
    internal static BitmapSource ReadOrFetch(string key,string title,string steamId,bool retry,string minecraftJar,bool online,string cacheRoot=null) {
        cacheRoot=cacheRoot??folder;
        Directory.CreateDirectory(cacheRoot);
        string hash;
        using(var sha=SHA256.Create()) hash=BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(key))).Replace("-","");
        string path=Path.Combine(cacheRoot,hash+".png"), miss=path+".retry";
        if(!retry && File.Exists(path)) {
            try {return Decode(File.ReadAllBytes(path));} catch(Exception) {File.Delete(path);}
        }
        string tmp=Path.Combine(cacheRoot,Guid.NewGuid().ToString("N")+".tmp");
        try {
            BitmapSource image=null;
            bool unavailable=false;
            if(minecraftJar!=null) {
                try {image=MinecraftIcon(minecraftJar);} catch(IOException) {} catch(InvalidDataException) {}
            }
            if(image==null && (!online || (!retry && WaitBeforeRetry(miss)))) return null;
            if(image==null) {
                try {
                    string uri=steamId==null && Normalize(title)=="minecraft"
                        ? "https://resources.download.minecraft.net/af/"+MinecraftWindowIcon : SearchSteam(title,steamId);
                    if(uri!=null) image=Decode(Fetch(uri,512000));
                } catch(WebException e) {unavailable=true;if(e.Response!=null)e.Response.Dispose();}
            }
            if(image==null && steamId==null && Normalize(title)!="minecraft") {
                try {
                    string uri=MacGameIcon(title);
                    if(uri!=null) image=Decode(Fetch(uri,512000));
                } catch(WebException e) {unavailable=true;if(e.Response!=null)e.Response.Dispose();}
            }
            if(image==null) {
                File.WriteAllText(miss,unavailable ? "offline" : "missing");
                return null;
            }
            try {
                var encoder=new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
                using(var stream=File.Create(tmp)) encoder.Save(stream);
                if(retry && File.Exists(path)) File.Replace(tmp,path,null);
                else if(!File.Exists(path)) File.Move(tmp,path);
            } catch(IOException) {} catch(UnauthorizedAccessException) {}
            return image;
        } catch(Exception) {
            File.WriteAllText(miss,"offline"); return null;
        } finally { if(File.Exists(tmp)) File.Delete(tmp); }
    }
}
