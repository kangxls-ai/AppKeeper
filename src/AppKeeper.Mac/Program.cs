using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Threading;
using System.Diagnostics;
using System.Security.Cryptography;

namespace AppKeeper;

internal static class Program
{
    [STAThread]
    internal static int Main(string[] args)
    {
        if (args.Contains("--self-test")) return MacSelfTest.Run().GetAwaiter().GetResult();
        if (args.Contains("--ui-smoke")) MacPlatform.TestRoot=Path.Combine(Path.GetTempPath(),"EasyAppKeeper-ui-"+Guid.NewGuid().ToString("N"));
        if (!OperatingSystem.IsMacOS()) { Console.Error.WriteLine("EasyAppKeeper Mac 需要 macOS；可使用 --self-test 运行共享逻辑检查。"); return 1; }
        try
        {
            var paths=EntryInbox.Arguments(args);
            if(paths.Length>0)
            {
                new EntryInbox(MacPlatform.Root).Enqueue(paths);
                try{using var probe=new FileStream(Path.Combine(MacPlatform.Root,"instance.lock"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);}
                catch(IOException){return 0;}
            }
        }
        catch(Exception error) when(error is IOException or UnauthorizedAccessException or ArgumentException){Console.Error.WriteLine(error.Message);return 1;}
        var software=args.Contains("--software-rendering")||args.Contains("--ui-smoke");
        return AppBuilder.Configure<MacApp>().UsePlatformDetect()
            .With(new AvaloniaNativePlatformOptions{AppSandboxEnabled=false,RenderingMode=software?[AvaloniaNativeRenderingMode.Software]:[AvaloniaNativeRenderingMode.Metal,AvaloniaNativeRenderingMode.OpenGl,AvaloniaNativeRenderingMode.Software]})
            .LogToTrace().StartWithClassicDesktopLifetime(args, ShutdownMode.OnExplicitShutdown);
    }
}

public sealed class MacApp : Application
{
    private TrayIcon? tray;
    private Process? hotkey;
    private MacWindow? window;
    private FloatingWindow? ball;
    private PigeonService? pigeon;
    private AgendaLanSync? sync;
    private FileStream? instance;
    public override void Initialize()
    {
        Styles.Add(new FluentTheme());
        Resources.ThemeDictionaries[ThemeVariant.Light]=new ResourceDictionary{{"MacControl",new SolidColorBrush(Color.Parse("#ffffff"))},{"MacLine",new SolidColorBrush(Color.Parse("#dce3ee"))}};
        Resources.ThemeDictionaries[ThemeVariant.Dark]=new ResourceDictionary{{"MacControl",new SolidColorBrush(Color.Parse("#2b374c"))},{"MacLine",new SolidColorBrush(Color.Parse("#3a4961"))}};
        Styles.Add(new Style(x=>x.OfType<Button>()){Setters={new Setter(Button.CornerRadiusProperty,new CornerRadius(8)),new Setter(Button.BackgroundProperty,new DynamicResourceExtension("MacControl")),new Setter(Button.BorderBrushProperty,new DynamicResourceExtension("MacLine")),new Setter(Button.BorderThicknessProperty,new Thickness(1)),new Setter(Button.FontSizeProperty,13d),new Setter(Button.MarginProperty,new Thickness(0,0,5,4))}});
        Styles.Add(new Style(x=>x.OfType<TextBox>()){Setters={new Setter(TextBox.CornerRadiusProperty,new CornerRadius(9)),new Setter(TextBox.BorderBrushProperty,new DynamicResourceExtension("MacLine")),new Setter(TextBox.FontSizeProperty,14d)}});
        Styles.Add(new Style(x=>x.OfType<Button>().Class("primary")){Setters={new Setter(Button.BackgroundProperty,new SolidColorBrush(Color.Parse("#477ec7"))),new Setter(Button.ForegroundProperty,Brushes.White),new Setter(Button.BorderThicknessProperty,new Thickness(0))}});
    }
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var state = new MacState(MacPlatform.Root);
            if(MacPlatform.TestRoot!=null)
            {
                var sample=Path.Combine(state.Root,"示例资料.txt");File.WriteAllText(sample,"界面验收示例数据");state.Data.Tags.Add("办公");state.Add(sample,"办公");state.Visit(state.Data.Entries[0]);
                ModuleFiles.Write(Path.Combine(state.Root,"AppKeeper.pigeon.json"),new PigeonData{Peers=[new("ui-peer","示例 Windows 电脑","127.0.0.2",1,"paired",false)],Items=[new(Guid.NewGuid(),"ui-peer",false,"text","中文消息与部分选择复制\n第二行",DateTimeOffset.Now,"saved",Unread:true),new(Guid.NewGuid(),"ui-peer",false,"file","示例资料.txt",DateTimeOffset.Now,"saved",Path:sample)]});
            }
            try { instance = new FileStream(Path.Combine(state.Root,"instance.lock"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None); }
            catch(IOException) { desktop.Shutdown(); return; }
            pigeon = new PigeonService(state.Root);
            var agenda = new AgendaStore(state.Root); sync = new AgendaLanSync(agenda,state.Root);
            if(MacPlatform.TestRoot!=null)agenda.Append("create","ui-task",new{title="检查 Mac 界面与快捷键",category="验收",deadline=AgendaStore.Iso(DateTimeOffset.Now.AddHours(2)),deadlineHasTime=true});
            window = new MacWindow(state,pigeon,agenda,sync); desktop.MainWindow = window;
            if(MacPlatform.TestRoot==null)_=ConfigureFinder();
            ball = new FloatingWindow(state,()=>window.Reveal(true));
            window.PreferencesSaved += () => { UpdateHotkey(state); if(state.Settings.Ball)ball.Show();else ball.Hide(); };
            var menu = new NativeMenu();
            var open = new NativeMenuItem("打开快捷应用"); open.Click += (_,_)=>window.Reveal(true); menu.Items.Add(open);
            var prefs = new NativeMenuItem("偏好设置…"); prefs.Click += (_,_)=>{window.Reveal();window.Preferences();}; menu.Items.Add(prefs);
            menu.Items.Add(new NativeMenuItemSeparator());
            var quit = new NativeMenuItem("退出 EasyAppKeeper"); quit.Click += (_,_)=>desktop.Shutdown(); menu.Items.Add(quit);
            using var icon = Avalonia.Platform.AssetLoader.Open(new Uri("avares://EasyAppKeeper/Assets/AppKeeper.ico"));
            tray = new TrayIcon { Icon = new WindowIcon(icon), ToolTipText="EasyAppKeeper", Menu=menu, IsVisible=true };
            tray.Clicked += (_,_)=>window.Reveal(true); TrayIcon.SetIcons(this,new TrayIcons { tray });
            UpdateHotkey(state);
            if(state.Settings.Ball) ball.Show();
            if(!desktop.Args!.Contains("--background")) window.Show();
            _ = StartSync();
            if(desktop.Args.Contains("--ui-smoke")) Dispatcher.UIThread.Post(async()=>
            {
                try { Console.WriteLine("UI: starting application scan");await window.ScanApps();Console.WriteLine("UI: scan complete");await window.Smoke();Console.WriteLine("PASS: Mac desktop view navigation, repeated launcher rendering, narrow/wide layouts and screenshots");desktop.Shutdown(0); }
                catch(Exception e){Console.Error.WriteLine(e);desktop.Shutdown(1);}
            });
            else _ = window.ScanApps();
            desktop.Exit += (_,_)=> { window.FinalClose=true;ball.FinalClose=true;hotkey?.Kill();hotkey?.Dispose();tray.Dispose();sync.Dispose();pigeon.Dispose();instance.Dispose(); };
        }
        base.OnFrameworkInitializationCompleted();
    }
    private async Task StartSync() { try { await sync!.StartAsync(); } catch(Exception e) { Dispatcher.UIThread.Post(()=>window!.Status(e.Message)); } }
    private async Task ConfigureFinder(){try{await window!.ApplyFinderIntegration();}catch(Exception e){window!.Status("访达右键设置失败："+e.Message);}}
    private void UpdateHotkey(MacState state)
    {
        if(hotkey is { HasExited:false }) hotkey.Kill(); hotkey?.Dispose();
        var start = new ProcessStartInfo(MacPlatform.Helper) { RedirectStandardOutput=true,RedirectStandardError=true,UseShellExecute=false };
        foreach(var arg in new[]{"--hotkey",state.Settings.HotkeyCode.ToString(),state.Settings.HotkeyModifiers.ToString()}) start.ArgumentList.Add(arg);
        hotkey=Process.Start(start);
        if(hotkey==null)return;
        hotkey.OutputDataReceived+=(_,e)=>{if(e.Data=="SHOW")Dispatcher.UIThread.Post(()=>window!.Reveal());};
        hotkey.ErrorDataReceived+=(_,e)=>{if(!string.IsNullOrWhiteSpace(e.Data))Dispatcher.UIThread.Post(()=>window!.Status(e.Data));};
        hotkey.BeginOutputReadLine();hotkey.BeginErrorReadLine();
    }
}

internal static class MacSelfTest
{
    internal static async Task<int> Run()
    {
        var root=Path.Combine(Path.GetTempPath(),"EasyAppKeeper-mac-test-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        try
        {
            if(!OperatingSystem.IsMacOS())Environment.SetEnvironmentVariable("APPKEEPER_MAC_TEST_KEY",Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
            var state=new MacState(Path.Combine(root,"settings"));
            var sample=Path.Combine(root,"sample.txt");File.WriteAllText(sample,"sample");state.Add(sample,null);state.Data.Tags.Add("办公");Layout.AssignTag(state.Data,state.Data.Entries[0],"办公");
            state.Visit(state.Data.Entries[0]);state.Settings.RecordVisits=false;state.Visit(state.Data.Entries[0]);
            if(new MacState(state.Root).Settings.Visits.Count!=1 || !Pinyin.Matches("计算器","jisuanqi"))throw new Exception("Mac visits or pinyin failed");
            MacLauncherSelfTest.Run(root);
            EntryInboxSelfTest.Run(root);
            await MacFinderSelfTest.Run(root);
            await ModuleSelfTest.Run(root);
            Console.WriteLine("PASS: launcher pins, drop path normalization, gesture debounce, scoped recent history, pinyin, Pigeon pairing/offline text/ZIP/resume/unread, SQLite, MarkDo encrypted phone sync");return 0;
        }
        catch(Exception e){Console.Error.WriteLine(e);return 1;}
        finally{Directory.Delete(root,true);}
    }
}
