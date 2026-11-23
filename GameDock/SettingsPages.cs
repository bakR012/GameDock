using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

partial class GameDock {
    const string MinecraftEntry="minecraft:choice";
    const string LauncherSetting="MinecraftLauncher";
    MinecraftLauncher minecraft=ReadLauncher();
    List<MinecraftLauncher> launchers;
    bool minecraftDetails,loadingLaunchers,checkingUpdate,failedUpdate,launcherError;
    bool checkAtStartup=Preference("CheckUpdatesAtStartup",1,0,1)==1;
    Version published=PreviousRelease();
    static Version PreviousRelease() {
        Version version;
        Version.TryParse(ReadSetting(prefs,"LatestStableVersion","") as string,out version);
        return version;
    }
    static readonly BitmapSource MinecraftLogo=ReadMinecraftLogo();

    static BitmapSource ReadMinecraftLogo() {
        using(var stream=typeof(GameDock).Assembly.GetManifestResourceStream("GameDock.Minecraft.png")) {
            var image=BitmapFrame.Create(stream,BitmapCreateOptions.PreservePixelFormat,BitmapCacheOption.OnLoad);
            image.Freeze(); return image;
        }
    }
    static MinecraftLauncher ReadLauncher() {
        var stored=ReadSetting(prefs,LauncherSetting,null) as string[];
        return stored!=null && stored.Length==2 && !String.IsNullOrWhiteSpace(stored[1]) ? new MinecraftLauncher(stored[0],stored[1]) : null;
    }
    bool SelectedMinecraft(string id) {return id==MinecraftEntry;}
    bool ExistingMinecraft(string id) {
        if(host.automatic) {
            InstalledGame game;
            return id=="prism-minecraft" || (host.detected.Games.TryGetValue(id,out game) && GameArtwork.Normalize(game.Title)=="minecraft") ||
                (DockPins.IsPin(id) && host.pins.IsMinecraft(id));
        }
        ManualGame manual;
        return host.manualGames.TryGetValue(id,out manual) && GameArtwork.Normalize(manual.Title)=="minecraft";
    }
    string[] WithMinecraft(string[] entries,string[] order) {
        if(host.minecraft==null) return entries;
        return MinecraftEntries(entries,order,HiddenNames().Contains(MinecraftEntry,Paths),ExistingMinecraft);
    }
    internal static string[] MinecraftEntries(string[] entries,string[] order,bool hidden,Func<string,bool> existing) {
        var list=entries.Where(id=>!existing(id)).ToList();
        if(hidden) return list.ToArray();
        list.Add(MinecraftEntry);
        var ranked=order.Select(id=>existing(id)?MinecraftEntry:id).Intersect(list,Paths);
        return ranked.Concat(list.Except(ranked,Paths)).ToArray();
    }
    void OpenMinecraftSettings() {
        host.settingsPage=2;host.minecraftDetails=true;OpenSettings();host.DrawSettings();
    }
    bool resolvingProfile;
    async void OpenSelectedMinecraft() {
        var choice=minecraft;
        if(choice==null) return;
        if(choice.IsShell) {Launch(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),"explorer.exe"),"\""+choice.Target+"\""); return;}
        if(!File.Exists(choice.Target)) {
            Alert(T("تعذّر العثور على اللانشر المحفوظ. اختر موقعه الجديد.","The saved launcher could not be found. Choose its new location."));
            OpenMinecraftSettings(); return;
        }
        string arguments="";
        if(lastMinecraftProfile && choice.CanChooseProfile) {
            if(resolvingProfile) return;
            resolvingProfile=true;
            try {arguments=await Task.Run(()=>choice.LastProfileArguments());}
            catch(Exception) {arguments="";}
            finally {resolvingProfile=false;}
            if(closing || choice!=minecraft) return;
        }
        Launch(choice.Target,arguments);
    }
    void SetLauncher(MinecraftLauncher choice) {
        try {
            Registry.SetValue(prefs,LauncherSetting,choice==null ? new string[0] : new[]{choice.Name,choice.Target},RegistryValueKind.MultiString);
            minecraft=choice;
            if(choice!=null) Registry.SetValue(HiddenKey,OrderName,HiddenNames().Where(id=>!Paths.Equals(id,MinecraftEntry)).ToArray(),RegistryValueKind.MultiString);
            Refresh(); DrawSettings();
        } catch(Exception e) {Alert(e.Message);}
    }
    void PickLauncher() {
        var pick=new OpenFileDialog {Title=T("اختيار لانشر Minecraft","Choose a Minecraft launcher"),
            Filter=T("لانشر أو اختصار","Launcher or shortcut")+"|*.exe;*.lnk;*.jar",DereferenceLinks=false,CheckFileExists=true};
        if(pick.ShowDialog(settingsWindow)==true && MinecraftLauncher.SupportsFile(pick.FileName))
            SetLauncher(new MinecraftLauncher(Path.GetFileNameWithoutExtension(pick.FileName),pick.FileName));
    }
    async void LoadLaunchers() {
        if(loadingLaunchers) return;
        loadingLaunchers=true; launcherError=false;
        try {launchers=await Task.Run(()=>MinecraftLauncher.Installed());}
        catch(Exception) {launchers=new List<MinecraftLauncher>();launcherError=true;}
        finally {loadingLaunchers=false;}
        if(settingsWindow!=null && settingsPage==2 && minecraftDetails) DrawSettings();
    }
    static void Heading(StackPanel body,string title) {
        body.Children.Add(new TextBlock {Text=title,FontSize=20,FontWeight=FontWeights.SemiBold,Margin=new Thickness(2,0,2,10)});
    }
    static void Detail(StackPanel body,string text) {
        body.Children.Add(new TextBlock {Text=text,TextWrapping=TextWrapping.Wrap,Margin=new Thickness(3,4,3,8)});
    }
    void DrawGameSettings(StackPanel body) {
        if(!minecraftDetails) {
            var card=SettingButton("Minecraft",delegate {minecraftDetails=true; DrawSettings();},false);
            var content=new StackPanel {Orientation=Orientation.Horizontal,VerticalAlignment=VerticalAlignment.Center};
            content.Children.Add(new Image {Source=MinecraftLogo,Width=36,Height=36,Margin=new Thickness(6)});
            var labels=new StackPanel {Margin=new Thickness(8,3,8,3)};
            labels.Children.Add(new TextBlock {Text="Minecraft",FontWeight=FontWeights.SemiBold,FontSize=15});
            labels.Children.Add(new TextBlock {Text=T("اللانشر وخيارات التشغيل","Launcher and launch options")});
            content.Children.Add(labels); card.Content=content;
            System.Windows.Automation.AutomationProperties.SetName(card,T("إعدادات Minecraft","Minecraft settings"));
            body.Children.Add(card); return;
        }
        body.Children.Add(SettingButton(T("العودة إلى الألعاب","Back to games"),delegate {minecraftDetails=false;DrawSettings();},false));
        Detail(body,minecraft==null ? T("اللانشر الحالي · تلقائي","Current launcher · Automatic") : T("اللانشر الحالي · ","Current launcher · ")+minecraft.Name);
        if(launchers==null && !loadingLaunchers) LoadLaunchers();
        if(loadingLaunchers) Detail(body,T("جارٍ البحث عن اللانشرات المثبّتة…","Finding installed launchers…"));
        else if(launchers!=null) {
            var options=launchers.ToList();
            if(minecraft!=null && !options.Any(l=>Paths.Equals(l.Target,minecraft.Target))) options.Add(minecraft);
            Section(body,T("تشغيل Minecraft بواسطة","Launch Minecraft with"),
                new[]{T("تلقائي","Automatic")}.Concat(options.Select(l=>l.Name)).ToArray(),
                minecraft==null ? 0 : options.FindIndex(l=>Paths.Equals(l.Target,minecraft.Target))+1,
                i=>SetLauncher(i==0?null:options[i-1]));
            if(launcherError || options.Count==0) Detail(body,T("يمكنك اختيار ملف اللانشر أو اختصاره مباشرة.","Choose a launcher file or shortcut below."));
        }
        var actions=new WrapPanel();
        actions.Children.Add(SettingButton(T("اختيار من الجهاز…","Browse…"),PickLauncher,false));
        actions.Children.Add(SettingButton(T("إعادة البحث","Scan again"),delegate {if(!loadingLaunchers){launchers=null;DrawSettings();}},false));
        body.Children.Add(actions);
        if(minecraft!=null ? minecraft.CanChooseProfile : automatic && detected.Games.ContainsKey("prism-minecraft")) {
            Section(body,T("عند فتح Prism","When opening Prism"),
                new[]{T("اختيار النسخة","Choose a profile"),T("آخر نسخة لعبتها","Last played profile")},lastMinecraftProfile?1:0,
                delegate(int i){lastMinecraftProfile=i==1; SavePreference("LastMinecraftProfile",i);});
        }
    }
    void StartupUpdateCheck() {
        if(!checkAtStartup) return;
        var ticks=ReadSetting(prefs,"LastUpdateCheckUtc",null);
        long elapsed=ticks is long ? DateTime.UtcNow.Ticks-(long)ticks : Int64.MaxValue;
        if(elapsed<0 || elapsed>=TimeSpan.TicksPerDay) CheckUpdates(false);
    }
    async void CheckUpdates(bool manual) {
        if(checkingUpdate) return;
        checkingUpdate=true; failedUpdate=false;
        if(settingsWindow!=null && settingsPage==4) DrawSettings();
        try {
            published=await ProjectInfo.Check();
            try {
                Registry.SetValue(prefs,"LatestStableVersion",published.ToString());
                Registry.SetValue(prefs,"LastUpdateCheckUtc",DateTime.UtcNow.Ticks,RegistryValueKind.QWord);
            } catch(UnauthorizedAccessException) {} catch(System.Security.SecurityException) {}
        }
        catch(Exception) {failedUpdate=true;}
        finally {checkingUpdate=false;}
        if(settingsWindow!=null && (settingsPage==4 || !manual && published!=null && published>ProjectInfo.Current)) DrawSettings();
    }
    void DrawAbout(StackPanel body) {
        Detail(body,"GameDock  "+ProjectInfo.Current.ToString(3)+"\n"+ProjectInfo.Copyright);
        var check=SettingButton(checkingUpdate?T("جارٍ التحقق…","Checking…"):T("التحقق من وجود تحديثات","Check for new updates"),delegate {CheckUpdates(true);},false);
        check.IsEnabled=!checkingUpdate; body.Children.Add(check);
        if(published!=null && published>ProjectInfo.Current) {
            Detail(body,T("يتوفر إصدار جديد · ","New version available · ")+published);
            body.Children.Add(SettingButton(T("فتح صفحة التحديث","Open update page"),delegate {Launch(ProjectInfo.Repository+"/releases/tag/v"+published);},false));
        } else if(published!=null && !failedUpdate && !checkingUpdate) Detail(body,T("لديك أحدث إصدار.","You’re up to date."));
        if(failedUpdate) {
            Detail(body,T("تعذّر الاتصال بـ GitHub. أعد المحاولة أو افتح صفحة الإصدارات.","Could not reach GitHub. Try again or open the releases page."));
            body.Children.Add(SettingButton(T("صفحة الإصدارات","Releases"),delegate {Launch(ProjectInfo.Repository+"/releases");},false));
        }
        AddSwitch(body,T("تحقّق عند بدء التشغيل","Check on startup"),checkAtStartup,delegate(bool on){checkAtStartup=on;SavePreference("CheckUpdatesAtStartup",on?1:0);});
        Detail(body,T("شيفرة GameDock مفتوحة المصدر بموجب GPLv3.","GameDock source code is available under GPLv3."));
        var links=new WrapPanel();
        links.Children.Add(SettingButton("GitHub",delegate {Launch(ProjectInfo.Repository);},false));
        links.Children.Add(SettingButton(T("عرض الترخيص","View license"),delegate {
            string text;
            using(var stream=typeof(GameDock).Assembly.GetManifestResourceStream("GameDock.LICENSE"))
            using(var reader=new StreamReader(stream)) text=reader.ReadToEnd();
            var license=new Window {Owner=settingsWindow,Title="GNU General Public License v3",Width=Math.Min(610,settingsAnchor.WorkArea.Width),
                Height=Math.Min(450,settingsAnchor.WorkArea.Height),WindowStartupLocation=WindowStartupLocation.CenterOwner,ShowInTaskbar=false,
                Content=new TextBox {Text=text,IsReadOnly=true,TextWrapping=TextWrapping.Wrap,Padding=new Thickness(12),VerticalScrollBarVisibility=ScrollBarVisibility.Auto}};
            license.ShowDialog();
        },false)); body.Children.Add(links);
    }
}
