using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Security.AccessControl;
using System.Security.Principal;
using System.ServiceProcess;
using Microsoft.Win32;

// This entry point runs only from the explicit settings / uninstall actions.
internal static class FpsSetup {
    const string Service="GameDockFrameMeter";
    static string Folder {get{return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),"GameDock Frame Meter");}}
    static string Binary {get{return Path.Combine(Folder,"GameDockFrameMeter.exe");}}
    static void Sc(string args) {
        var info=new ProcessStartInfo(Path.Combine(Environment.SystemDirectory,"sc.exe"),args) {UseShellExecute=false,CreateNoWindow=true};
        using(var p=Process.Start(info)) {p.WaitForExit();if(p.ExitCode!=0) throw new InvalidOperationException("Service configuration failed: "+p.ExitCode);}
    }
    static void CheckFolder() {
        if((File.GetAttributes(Folder)&FileAttributes.ReparsePoint)!=0) throw new IOException("Invalid service folder");
        var acl=Directory.GetAccessControl(Folder);
        var admins=new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid,null);
        var system=new SecurityIdentifier(WellKnownSidType.LocalSystemSid,null);
        var owner=acl.GetOwner(typeof(SecurityIdentifier));
        if(!owner.Equals(admins) && !owner.Equals(system)) throw new IOException("Unexpected service folder owner");
        foreach(FileSystemAccessRule rule in acl.GetAccessRules(true,true,typeof(SecurityIdentifier))) {
            if(rule.AccessControlType==AccessControlType.Allow && !rule.IdentityReference.Equals(admins) &&
                !rule.IdentityReference.Equals(system) && (rule.FileSystemRights &
                (FileSystemRights.Write|FileSystemRights.Delete|FileSystemRights.DeleteSubdirectoriesAndFiles|FileSystemRights.ChangePermissions|FileSystemRights.TakeOwnership))!=0)
                throw new IOException("Service folder is writable");
        }
    }
    internal static int Configure(bool remove) {
        try {
            var admin=new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid,null);
            if(!new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(admin)) return 5;
            bool existing;
            using(var key=Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\"+Service)) {
                existing=key!=null;
                if(existing && !String.Equals(key.GetValue("ImagePath") as string,"\""+Binary+"\"",StringComparison.OrdinalIgnoreCase)) return 13;
            }
            if(remove && !existing) return 0;
            if(!Directory.Exists(Folder)) {
                var security=new DirectorySecurity();security.SetAccessRuleProtection(true,false);security.SetOwner(admin);
                foreach(var sid in new[]{admin,new SecurityIdentifier(WellKnownSidType.LocalSystemSid,null),new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid,null),new SecurityIdentifier(WellKnownSidType.LocalServiceSid,null)}) {
                    var rights=sid.Equals(admin)||sid.IsWellKnown(WellKnownSidType.LocalSystemSid)?FileSystemRights.FullControl:FileSystemRights.ReadAndExecute;
                    security.AddAccessRule(new FileSystemAccessRule(sid,rights,InheritanceFlags.ContainerInherit|InheritanceFlags.ObjectInherit,PropagationFlags.None,AccessControlType.Allow));
                }
                Directory.CreateDirectory(Folder,security);
            }
            CheckFolder();
            if(existing) using(var svc=new ServiceController(Service)) {
                if(svc.Status!=ServiceControllerStatus.Stopped) {
                    if(svc.Status!=ServiceControllerStatus.StopPending) svc.Stop();
                    svc.WaitForStatus(ServiceControllerStatus.Stopped,TimeSpan.FromSeconds(15));
                }
            }
            if(remove) {Sc("delete "+Service);File.Delete(Binary);if(Directory.GetFileSystemEntries(Folder).Length==0) Directory.Delete(Folder);return 0;}
            string temp=Path.Combine(Folder,Guid.NewGuid().ToString("N")+".tmp");
            try {
                using(var input=Assembly.GetExecutingAssembly().GetManifestResourceStream("GameDock.FrameService.exe"))
                using(var output=new FileStream(temp,FileMode.CreateNew,FileAccess.Write,FileShare.None)) input.CopyTo(output);
                if(File.Exists(Binary)) File.Replace(temp,Binary,null);else File.Move(temp,Binary);
            } finally {if(File.Exists(temp)) File.Delete(temp);}
            if(!existing) Sc("create "+Service+" binPath= \"\\\"" +Binary+"\\\"\" start= demand obj= \"NT AUTHORITY\\LocalService\" DisplayName= \"GameDock FPS\"");
            Sc("sdset "+Service+" D:(A;;GA;;;SY)(A;;GA;;;BA)(A;;LCRP;;;IU)");
            return 0;
        } catch(Exception) {return 1;}
    }
}
