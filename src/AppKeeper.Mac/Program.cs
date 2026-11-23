using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Themes.Fluent;
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
        if (!OperatingSystem.IsMacOS()) { Console.Error.WriteLine("EasyAppKeeper Mac 需要 macOS；可使用 --self-test 运行共享逻辑检查。"); return 1; }
        return AppBuilder.Configure<MacApp>().UsePlatformDetect().LogToTrace().StartWithClassicDesktopLifetime(args, ShutdownMode.OnExplicitShutdown);
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
    public override void Initialize() => Styles.Add(new FluentTheme());
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var state = new MacState(MacPlatform.Root);
            try { instance = new FileStream(Path.Combine(state.Root,"instance.lock"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None); }
            catch(IOException) { MacPlatform.Open(Path.Combine(AppContext.BaseDirectory,"..","..")); desktop.Shutdown(); return; }
            pigeon = new PigeonService(state.Root);
            var agenda = new AgendaStore(state.Root); sync = new AgendaLanSync(agenda,state.Root);
            window = new MacWindow(state,pigeon,agenda,sync); desktop.MainWindow = window;
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
            _ = StartSync(); _ = window.ScanApps();
            desktop.Exit += (_,_)=> { window.FinalClose=true;ball.FinalClose=true;hotkey?.Kill();hotkey?.Dispose();tray.Dispose();sync.Dispose();pigeon.Dispose();instance.Dispose(); };
        }
        base.OnFrameworkInitializationCompleted();
    }
    private async Task StartSync() { try { await sync!.StartAsync(); } catch(Exception e) { Dispatcher.UIThread.Post(()=>window!.Status(e.Message)); } }
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
            await ModuleSelfTest.Run(root);
            Console.WriteLine("PASS: Mac local storage, scoped recent history, pinyin, Pigeon pairing/offline text/ZIP/resume/unread, SQLite, MarkDo encrypted phone sync");return 0;
        }
        catch(Exception e){Console.Error.WriteLine(e);return 1;}
        finally{Directory.Delete(root,true);}
    }
}
