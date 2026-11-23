using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
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
    internal static Task<Version> Check() {
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
                return PublishedVersion(Encoding.UTF8.GetString(buffer.ToArray()));
            }
        });
    }
}
