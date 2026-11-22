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
    static readonly string folder=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"GameDock","Artwork");
    static readonly SemaphoreSlim workers=new SemaphoreSlim(2);
    static readonly Dictionary<string,Task<BitmapSource>> pending=new Dictionary<string,Task<BitmapSource>>();
    static readonly Dictionary<string,BitmapSource> recent=new Dictionary<string,BitmapSource>();
    static readonly Queue<string> order=new Queue<string>();

    static GameArtwork() { ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12; }
    internal static string Normalize(string title) {
        return Regex.Replace(title.Normalize(NormalizationForm.FormKC).ToLowerInvariant(),@"[^\p{L}\p{Nd}]","");
    }
    internal static async Task<BitmapSource> Get(string title,string steamId) {
        string key=steamId==null ? "title-"+Normalize(title) : "steam-"+steamId;
        Task<BitmapSource> task;
        lock(pending) {
            BitmapSource cached;
            if(recent.TryGetValue(key,out cached)) return cached;
            if(!pending.TryGetValue(key,out task)) {
                task=Load(key,title,steamId);
                pending.Add(key,task);
            }
        }
        var result=await task.ConfigureAwait(false);
        lock(pending) {
            if(pending.Remove(key) && result!=null) {
                if(order.Count>=64) recent.Remove(order.Dequeue());
                recent[key]=result; order.Enqueue(key);
            }
        }
        return result;
    }
    static async Task<BitmapSource> Load(string key,string title,string steamId) {
        await workers.WaitAsync().ConfigureAwait(false);
        try { return await Task.Run(()=>ReadOrFetch(key,title,steamId)).ConfigureAwait(false); }
        catch(Exception) { return null; }
        finally { workers.Release(); }
    }
    static BitmapSource Decode(byte[] data) {
        using(var input=new MemoryStream(data)) {
            var image=new BitmapImage();
            image.BeginInit(); image.CacheOption=BitmapCacheOption.OnLoad;
            image.DecodePixelWidth=96; image.StreamSource=input; image.EndInit();
            if(image.PixelHeight<4 || image.PixelHeight>512) throw new InvalidDataException();
            // Detach the thumbnail from the encoded source and its MemoryStream.
            var small=new WriteableBitmap(image);
            small.Freeze(); return small;
        }
    }
    static byte[] Fetch(string url,int limit) {
        var request=(HttpWebRequest)WebRequest.Create(url);
        request.Timeout=5500; request.ReadWriteTimeout=5500; request.AllowAutoRedirect=false;
        request.UserAgent="GameDock/1.5 (github.com/bakR012/GameDock)";
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
    static Dictionary<string,object> Json(string url) {
        return new JavaScriptSerializer().Deserialize<Dictionary<string,object>>(Encoding.UTF8.GetString(Fetch(url,196608)));
    }
    static string Value(Dictionary<string,object> obj,string field) {
        object value; return obj!=null && obj.TryGetValue(field,out value) ? Convert.ToString(value) : "";
    }
    static string SearchSteam(string title) {
        object items;
        var result=Json("https://store.steampowered.com/api/storesearch/?l=english&cc=US&term="+Uri.EscapeDataString(title));
        if(!result.TryGetValue("items",out items) || !(items is System.Collections.IEnumerable)) return null;
        foreach(var row in ((System.Collections.IEnumerable)items).Cast<object>().OfType<Dictionary<string,object>>()) {
            if(Normalize(Value(row,"name"))!=Normalize(title)) continue;
            string id=Value(row,"id");
                        if(!Regex.IsMatch(id,@"^\d{1,10}$")) continue;
            Uri uri;
            if(Uri.TryCreate(Value(row,"tiny_image"),UriKind.Absolute,out uri) && uri.Scheme=="https" &&
                (uri.Host.EndsWith(".steamstatic.com",StringComparison.OrdinalIgnoreCase) || uri.Host.EndsWith(".steamusercontent.com",StringComparison.OrdinalIgnoreCase)) &&
                uri.AbsolutePath.Contains("/apps/"+id+"/")) return uri.AbsoluteUri;
            return SteamCover(id);
        }
        return null;
    }
    static string SteamCover(string id) {
        return "https://shared.fastly.steamstatic.com/store_item_assets/steam/apps/"+id+"/capsule_184x69.jpg";
    }
    static BitmapSource SteamLocal(string id) {
        if(id==null || !Regex.IsMatch(id,@"^\d{1,10}$")) return null;
        string root=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),"Steam");
        try { root=Microsoft.Win32.Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam","SteamPath",null) as string ?? root; }
        catch(System.Security.SecurityException) {} catch(UnauthorizedAccessException) {}
        string cache=Path.Combine(root,@"appcache\librarycache");
        foreach(string path in new[]{Path.Combine(cache,id,"library_600x900.jpg"),Path.Combine(cache,id,"header.jpg"),Path.Combine(cache,id+"_library_600x900.jpg")}) {
            var file=new FileInfo(path);
            if(!file.Exists || file.Length>2048000) continue;
            try {return Decode(File.ReadAllBytes(path));} catch(Exception) {}
        }
        return null;
    }
    static string Wiki(string title) {
        foreach(string page in new[]{title+" (video game)",title}) {
            Dictionary<string,object> row;
            try {row=Json("https://en.wikipedia.org/api/rest_v1/page/summary/"+Uri.EscapeDataString(page.Replace(' ','_')));}
            catch(WebException e) {var r=e.Response as HttpWebResponse;if(r==null || r.StatusCode!=HttpStatusCode.NotFound) throw; r.Dispose();continue;}
            object thumbnail; Uri uri;
            if(Normalize(Value(row,"title"))!=Normalize(page) || !Regex.IsMatch(Value(row,"description")+" "+Value(row,"extract"),@"\bvideo game\b",RegexOptions.IgnoreCase)) continue;
            if(row.TryGetValue("thumbnail",out thumbnail) && Uri.TryCreate(Value(thumbnail as Dictionary<string,object>,"source"),UriKind.Absolute,out uri)
                && uri.Scheme=="https" && uri.Host=="upload.wikimedia.org") return uri.AbsoluteUri;
        }
        return null;
    }
    static BitmapSource ReadOrFetch(string key,string title,string steamId) {
        Directory.CreateDirectory(folder);
        string hash;
        using(var sha=SHA256.Create()) hash=BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(key))).Replace("-","");
        string path=Path.Combine(folder,hash+".png"), miss=path+".retry";
        if(File.Exists(path)) {
            try {return Decode(File.ReadAllBytes(path));} catch(Exception) {File.Delete(path);}
        }
        if(File.Exists(miss) && File.GetLastWriteTimeUtc(miss)>DateTime.UtcNow.AddHours(-12)) return null;
        string tmp=Path.Combine(folder,Guid.NewGuid().ToString("N")+".tmp");
        try {
            BitmapSource image=SteamLocal(steamId);
            try {
                string uri=image!=null ? null : steamId==null ? SearchSteam(title) : SteamCover(steamId);
                if(uri!=null) image=Decode(Fetch(uri,512000));
            } catch(WebException e) { if(e.Response!=null)e.Response.Dispose(); }
            if(image==null) {
                string uri=Wiki(title);
                if(uri!=null) image=Decode(Fetch(uri,512000));
            }
            if(image==null) {File.WriteAllText(miss,""); return null;}
            try {
                var encoder=new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
            using(var stream=File.Create(tmp)) encoder.Save(stream);
                if(!File.Exists(path)) File.Move(tmp,path);
            } catch(IOException) {} catch(UnauthorizedAccessException) {}
            return image;
        } catch(Exception) {
            File.WriteAllText(miss,""); File.SetLastWriteTimeUtc(miss,DateTime.UtcNow.AddHours(-12).AddMinutes(15)); return null;
        } finally { if(File.Exists(tmp)) File.Delete(tmp); }
    }
}
