using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

internal static class UpdateChecks {
    static void Require(bool ok,string reason) {if(!ok) throw new Exception(reason);}
    static void Downloads(string dir) {
        var contents=Encoding.UTF8.GetBytes("GameDock installer fixture");
        string digest;
        using(var sha=SHA256.Create()) digest=BitConverter.ToString(sha.ComputeHash(contents)).Replace("-","");
        string json="{\"tag_name\":\"v9.0.0\",\"draft\":false,\"prerelease\":false,\"assets\":[{\"name\":\"GameDock-Setup-9.0.0.exe\",\"size\":"+contents.Length+
            ",\"state\":\"uploaded\",\"digest\":\"sha256:"+digest+"\",\"browser_download_url\":\"https://github.com/bakR012/GameDock/releases/download/v9.0.0/GameDock-Setup-9.0.0.exe\"}]}";
        var release=ProjectInfo.Parse(json);
        Require(release.Download!=null && ProjectInfo.Parse(json.Replace("bakR012","someone")).Download==null &&
            ProjectInfo.Parse(json.Replace("sha256:","unknown:")).Download==null,"Unexpected release identity");
        var file=Path.Combine(dir,"download");
        int progress=0;
        using(var output=File.Create(file)) ProjectInfo.CopyVerified(new MemoryStream(contents),output,release,i=>progress=i,CancellationToken.None);
        Require(progress==100 && File.ReadAllBytes(file).SequenceEqual(contents),"Verified bytes differ");
        foreach(var invalid in new[]{contents.Take(contents.Length-1).ToArray(),contents.Concat(new byte[]{0}).ToArray(),new byte[contents.Length]}) {
            try {using(var output=File.Create(file)) ProjectInfo.CopyVerified(new MemoryStream(invalid),output,release,i=>{},CancellationToken.None);}
            catch(InvalidDataException) {continue;}
            throw new Exception("Corrupt update accepted");
        }
        try {using(var output=File.Create(file)) ProjectInfo.CopyVerified(new MemoryStream(contents),output,release,i=>{},new CancellationToken(true));}
        catch(OperationCanceledException) {Console.WriteLine("PASS release origin, digest, truncation, oversize, cancellation and progress");return;}
        throw new Exception("Canceled transfer accepted");
    }
    static void Handoff(string dir,bool fail) {
        var exe=Path.Combine(dir,"GameDock.exe");var maintenance=Path.Combine(dir,"GameDock-Uninstall.exe");
        var metadata=Path.Combine(dir,"metadata");var version=Path.Combine(dir,"version");
        File.WriteAllText(exe,"old app");File.WriteAllText(maintenance,"old setup");File.WriteAllText(metadata,"old version");
        File.WriteAllText(version,"new version");if(fail) File.WriteAllText(Path.Combine(dir,"fail"),"");
        var id=Guid.NewGuid();string key=@"Local\GameDock.Update."+id.ToString("N");
        using(var loaded=new EventWaitHandle(false,EventResetMode.ManualReset,key+".loaded"))
        using(var quit=new EventWaitHandle(false,EventResetMode.ManualReset,key+".quit"))
        using(var exited=new EventWaitHandle(false,EventResetMode.ManualReset,key+".exit")) {
            bool rolledBack=false;
            try {
                Distribution.ReplaceAndLaunch(dir,Assembly.GetExecutingAssembly().Location,version,id,loaded,tx=>tx.Replace(metadata,version));
            } catch(IOException) {rolledBack=true;}
            finally {quit.Set();}
            Require(rolledBack==fail,"Unexpected update result");
            if(fail) Require(File.ReadAllText(exe)=="old app" && File.ReadAllText(maintenance)=="old setup" && File.ReadAllText(metadata)=="old version","Rollback incomplete");
            else Require(exited.WaitOne(5000) && File.ReadAllText(metadata)=="new version" && File.ReadAllText(maintenance)=="new version","Update not committed");
        }
        Console.WriteLine(fail?"PASS failed startup restores previous files and metadata":"PASS new process acknowledges startup before update is committed");
    }
    static int Main(string[] args) {
        if(args.Length==2 && args[0]=="--update-ready") {
            if(File.Exists("fail")) return 1;
            string key=@"Local\GameDock.Update."+args[1];
            using(var loaded=EventWaitHandle.OpenExisting(key+".loaded"))
            using(var quit=EventWaitHandle.OpenExisting(key+".quit"))
            using(var exit=EventWaitHandle.OpenExisting(key+".exit")) {loaded.Set();quit.WaitOne(8000);exit.Set();}
            return 0;
        }
        var folder=Path.Combine(Path.GetTempPath(),"GameDock-UpdateChecks-"+Guid.NewGuid().ToString("N"));
        try {
            Directory.CreateDirectory(folder);Downloads(folder);
            foreach(bool fail in new[]{false,true}) {var d=Directory.CreateDirectory(Path.Combine(folder,fail?"rollback":"success"));Handoff(d.FullName,fail);}
            return 0;
        } catch(Exception ex) {Console.Error.WriteLine(ex);return 1;}
        finally {
            for(int i=0;Directory.Exists(folder);i++) try {Directory.Delete(folder,true);} catch(IOException) {if(i==20) throw;Thread.Sleep(150);}
        }
    }
}
