using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Threading;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

internal static class ProjectInfo {
    internal const string Author="bakR";
    internal const string Repository="https://github.com/bakR012/GameDock";
    internal const string Copyright="© 2026 bakR";
    internal static readonly Version Current=new Version(FileVersionInfo.GetVersionInfo(typeof(ProjectInfo).Assembly.Location).FileVersion);
    internal static Version PublishedVersion(string json) {
        var data=new JavaScriptSerializer().Deserialize<Dictionary<string,object>>(json);
        object tag,draft,preview;
        if(data==null || !data.TryGetValue("tag_name",out tag) || !data.TryGetValue("draft",out draft) ||
            !data.TryGetValue("prerelease",out preview) || !Object.Equals(draft,false) || !Object.Equals(preview,false))
            throw new InvalidDataException();
        string value=tag as string ?? "";
        Version version;
        if(!Regex.IsMatch(value,@"^v\d+\.\d+\.\d+$") || !Version.TryParse(value.Substring(1),out version)) throw new InvalidDataException();
        return version;
    }
    internal sealed class Release {
        internal Version Version;
        internal Uri Download;
        internal long Size;
        internal string Digest;
    }
    internal static Release Parse(string json) {
        var release=new Release {Version=PublishedVersion(json)};
        var data=new JavaScriptSerializer().Deserialize<Dictionary<string,object>>(json);
        object raw;
        if(!data.TryGetValue("assets",out raw) || !(raw is System.Collections.IEnumerable)) return release;
        string tag="v"+release.Version,name="GameDock-Setup-"+release.Version+".exe";
        string url=Repository+"/releases/download/"+tag+"/"+name;
        foreach(object asset in (System.Collections.IEnumerable)raw) {
            var fields=asset as Dictionary<string,object>;
            object file,link,state,size,digest;long bytes;
            if(fields==null || !fields.TryGetValue("name",out file) || !Object.Equals(file,name) ||
                !fields.TryGetValue("browser_download_url",out link) || !Object.Equals(url,link) ||
                !fields.TryGetValue("state",out state) || !Object.Equals(state,"uploaded") ||
                !fields.TryGetValue("size",out size) || !Int64.TryParse(Convert.ToString(size),out bytes) || bytes<=0 || bytes>64*1024*1024 ||
                !fields.TryGetValue("digest",out digest) || !(digest is string) || !Regex.IsMatch((string)digest,@"\Asha256:[0-9a-fA-F]{64}\z")) continue;
            release.Download=new Uri(url);release.Size=bytes;release.Digest=((string)digest).Substring(7);break;
        }
        return release;
    }
    internal static Task<Release> Check() {
        return Task.Run(delegate {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            var request=(HttpWebRequest)WebRequest.Create("https://api.github.com/repos/bakR012/GameDock/releases/latest");
            request.UserAgent="GameDock/"+Current.ToString(3);
            request.Accept="application/vnd.github+json"; request.Timeout=10000; request.ReadWriteTimeout=8000;
            using(var response=request.GetResponse())
            using(var stream=response.GetResponseStream())
            using(var buffer=new MemoryStream()) {
                byte[] chunk=new byte[4096]; int n;
                while((n=stream.Read(chunk,0,chunk.Length))>0) {
                    if(buffer.Length+n>1024*1024) throw new InvalidDataException();
                    buffer.Write(chunk,0,n);
                }
                return Parse(Encoding.UTF8.GetString(buffer.ToArray()));
            }
        });
    }
    internal static void CopyVerified(Stream input,FileStream output,Release release,Action<int> progress,CancellationToken stop) {
        byte[] block=new byte[65536];long length=0;int previous=-1;
        using(var sha=SHA256.Create()) {
            for(;;) {
                stop.ThrowIfCancellationRequested();
                int n=input.Read(block,0,block.Length);if(n==0) break;
                length+=n;if(length>release.Size) throw new InvalidDataException();
                output.Write(block,0,n);sha.TransformBlock(block,0,n,block,0);
                int percent=(int)(length*100/release.Size);
                if(previous!=percent) {previous=percent;progress(percent);}
            }
            sha.TransformFinalBlock(block,0,0);
            if(length!=release.Size || !String.Equals(BitConverter.ToString(sha.Hash).Replace("-",""),release.Digest,StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The installer did not match its published digest.");
            output.Flush(true);
        }
    }
    internal static string Download(Release release,Action<int> progress,CancellationToken stop) {
        if(release.Download==null || release.Version<=Current) throw new InvalidDataException();
        var folder=Path.Combine(Path.GetTempPath(),"GameDock-Update-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var file=Path.Combine(folder,"GameDock-Setup.exe");
        try {
            Uri url=release.Download;
            for(int i=0;i<5;i++) {
                var req=(HttpWebRequest)WebRequest.Create(url);req.AllowAutoRedirect=false;
                req.Timeout=15000;req.ReadWriteTimeout=20000;req.UserAgent="GameDock/"+Current;
                using(stop.Register(req.Abort))
                using(var response=(HttpWebResponse)req.GetResponse()) {
                    if((int)response.StatusCode>=300 && (int)response.StatusCode<400) {
                        Uri next;
                        if(!Uri.TryCreate(url,response.Headers["Location"],out next) || next.Scheme!="https" || !next.IsDefaultPort ||
                            next.UserInfo.Length!=0 || !(next.Host=="github.com" || next.Host.EndsWith(".githubusercontent.com",StringComparison.OrdinalIgnoreCase)))
                            throw new InvalidDataException();
                        url=next;continue;
                    }
                    if(response.StatusCode!=HttpStatusCode.OK) throw new IOException();
                    using(var input=response.GetResponseStream())
                    using(var output=new FileStream(file,FileMode.CreateNew,FileAccess.Write,FileShare.None)) CopyVerified(input,output,release,progress,stop);
                    if(new Version(FileVersionInfo.GetVersionInfo(file).FileVersion)!=new Version(release.Version.ToString(3)+".0")) throw new InvalidDataException();
                    return file;
                }
            }
            throw new IOException("Too many redirects");
        } catch {if(File.Exists(file)) File.Delete(file);Directory.Delete(folder);throw;}
    }
    internal static void ClearDownload(Guid id) {
        Task.Run(delegate {
            var dir=Path.Combine(Path.GetTempPath(),"GameDock-Update-"+id.ToString("N"));
            for(int i=0;i<15;i++) {
                try {
                    if(!Directory.Exists(dir) || (File.GetAttributes(dir)&FileAttributes.ReparsePoint)!=0) return;
                    File.Delete(Path.Combine(dir,"GameDock-Setup.exe"));Directory.Delete(dir);return;
                } catch(IOException) {} catch(UnauthorizedAccessException) {}
                Thread.Sleep(500);
            }
        });
    }
}
