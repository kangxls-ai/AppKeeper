using Microsoft.Win32;
using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace AppKeeper;

public partial class App : Application
{
    private readonly ProductProfile profile = ProductProfile.Current;
    private readonly LibraryStore store = new(ProductProfile.Current.DataDirectory);
    private Mutex? instance;
    private EventWaitHandle? showEvent;
    private RegisteredWaitHandle? showWait;
    private HwndSource? messages;
    private Forms.NotifyIcon? tray;
    private System.Drawing.Icon? trayIcon;
    private DispatcherTimer? scanTimer;
    private DispatcherTimer? reminderTimer;
    private PigeonService? pigeon;
    private AgendaStore? agenda;
    private AgendaLanSync? agendaSync;
    private RecentUsageService? recentUsage;
    private string moduleWarning = "";
    private DateTimeOffset previousReminderCheck = DateTimeOffset.Now;
    private MainWindow? launcher;
    private FloatingBall? ball;
    private Library data = new();
    private bool writable = true;
    private bool scanning;
    private bool hotkeyRegistered;
    private bool startupManaged;
    private string notice = "";
    private string scanNotice = "";
    private string hotkeyNotice = "";

    internal Library Data => data;
    internal string ProductName => profile.Name;
    internal bool IsStoreEdition => profile.IsStore;
    private int unreadRefreshQueued;
    internal PigeonService Pigeon
    {
        get
        {
            if (pigeon == null)
            {
                pigeon = new PigeonService(profile.DataDirectory);
                pigeon.UnreadChanged += PigeonUnreadChanged;
            }
            return pigeon;
        }
    }
    internal int PigeonUnreadTotal => pigeon?.UnreadCounts().Values.Sum() ?? 0;
    internal AgendaStore Agenda => agenda ??= new AgendaStore(profile.DataDirectory);
    internal AgendaLanSync AgendaSync => agendaSync ??= new AgendaLanSync(Agenda, profile.DataDirectory);
    internal RecentUsageService RecentUsage => recentUsage ??= new RecentUsageService(profile.DataDirectory);
    internal string Status => string.Join("　", new[] { notice, scanNotice, hotkeyNotice }.Where(text => text.Length > 0));

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (e.Args.Contains("--self-test"))
        {
            Shutdown(SelfTest.Run());
            return;
        }
        if (e.Args.Contains("--store-self-test"))
        {
            Shutdown(SelfTest.CheckStoreActivation(profile));
            return;
        }

        try { var paths=EntryInbox.Arguments(e.Args);if(paths.Length>0)Inbox.Enqueue(paths); }
        catch(Exception error) when(error is IOException or UnauthorizedAccessException or ArgumentException){Error(error.Message);Shutdown(1);return;}
        var background = e.Args.Contains("--background") || (profile.IsStore &&
            string.Equals(Path.GetFileName(Environment.ProcessPath), "EasyAppKeeper.Startup.exe", StringComparison.OrdinalIgnoreCase));
        const string instanceName = @"Local\AppKeeper.V1";
        using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        var name = instanceName + "." + identity.User?.Value + "." + EntryInbox.Scope(profile.DataDirectory);
        showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, name + ".Show");
        instance = new Mutex(false, name, out var first);
        if (!first)
        {
            if (!background)
                showEvent.Set();
            Shutdown();
            return;
        }

        try
        {
            if (profile.IsStore && !Directory.Exists(profile.DataDirectory))
            {
                if (!background)
                    Dialogs.OfferPortableMigration(profile);
                Directory.CreateDirectory(profile.DataDirectory);
            }
            data = store.Load(out var recovery);
            if (profile.IsStore && !File.Exists(store.DataPath))
                data.StartWithWindows = false;
            notice = recovery ?? "";
            if (recovery != null || !File.Exists(store.DataPath))
                store.Save(data);
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            writable = false;
            notice = error is InvalidDataException ? error.Message :
                profile.IsStore ? "无法写入商店版用户数据目录。" :
                    "程序目录无法写入。请将 AppKeeper 移到可写目录后重新运行。";
        }

        messages = new HwndSource(new HwndSourceParameters("AppKeeper Messages")
        {
            ParentWindow = new IntPtr(-3),
            WindowStyle = 0
        });
        messages.AddHook(OnMessage);
        RegisterShortcut();
        CreateTray();
        if (writable)
        {
            _ = StartAgendaSync();
            try { _ = RecentUsage; }
            catch (Exception error) { moduleWarning = "最近访问初始化失败：" + error.Message; }
            try { _ = Pigeon; }
            catch (Exception error) { moduleWarning = "飞鸽传书初始化失败：" + error.Message; }
            reminderTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
            reminderTimer.Tick += (_, _) => CheckAgendaReminders();
            reminderTimer.Start();
        }
        ball = new FloatingBall(ShowHomeFromBall, position =>
        {
            if (writable)
                Commit(library => library.Ball = position);
        });
        ball.Present(data.Ball);
        SystemEvents.DisplaySettingsChanged += DisplaySettingsChanged;
        if (writable)
        {
            ApplyAutostart();
            ApplyShellMenu();StartEntryInbox();
        }

        showWait = ThreadPool.RegisterWaitForSingleObject(showEvent, (_, _) =>
        {
            if (!Dispatcher.HasShutdownStarted)
                _ = Dispatcher.BeginInvoke(ShowLauncher);
        }, null, Timeout.Infinite, false);

        if (!background)
            ShowLauncher();
        else if (Status.Length > 0 || moduleWarning.Length > 0)
            tray!.ShowBalloonTip(5000, ProductName, Status + " " + moduleWarning, Forms.ToolTipIcon.Warning);

        if (writable)
            _ = Refresh();
        scanTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(10) };
        scanTimer.Tick += async (_, _) => await Refresh();
        scanTimer.Start();
    }

    private void CreateTray()
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("显示 " + ProductName, null, (_, _) => ShowLauncher());
        menu.Items.Add("设置", null, (_, _) => GetLauncher().ShowSettingsFromTray());
        menu.Items.Add("重新扫描应用", null, async (_, _) => await Refresh(showResult: true));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("退出 " + ProductName, null, (_, _) =>
        {
            launcher?.CloseForExit();
            Shutdown();
        });
        using var stream = GetResourceStream(new Uri("Assets/AppKeeper.ico", UriKind.Relative)).Stream;
        trayIcon = new System.Drawing.Icon(stream);
        tray = new Forms.NotifyIcon { Icon = trayIcon, Text = ProductName, ContextMenuStrip = menu, Visible = true };
        tray.MouseClick += (_, args) =>
        {
            if (args.Button == Forms.MouseButtons.Left)
                ToggleLauncher();
        };
    }

    private MainWindow GetLauncher() => launcher ??= new MainWindow(this);

    private void PigeonUnreadChanged()
    {
        if (Dispatcher.HasShutdownStarted || Interlocked.Exchange(ref unreadRefreshQueued, 1) != 0) return;
        _ = Dispatcher.BeginInvoke(() =>
        {
            Interlocked.Exchange(ref unreadRefreshQueued, 0);
            launcher?.UpdatePigeonUnread();
        });
    }

    private async Task StartAgendaSync()
    {
        try { await AgendaSync.StartAsync(); }
        catch (Exception error)
        {
            System.Diagnostics.Trace.TraceWarning("手机同步服务未启动：" + error.Message);
        }
    }

    private void CheckAgendaReminders()
    {
        var now = DateTimeOffset.Now;
        var from = previousReminderCheck;
        previousReminderCheck = now;
        try
        {
            var store = Agenda;
            if (!store.Reminders) return;
            var messages = store.Tasks().Where(task => task.Completed == null)
                .SelectMany(task => new[]
                {
                    (When: task.StartTime && task.Actual == null ? task.Start : null, Text: "开始 · " + task.Title),
                    (When: task.StartTime && task.Actual == null ? task.Start?.AddMinutes(30) : null, Text: "开始提醒 · " + task.Title),
                    (When: task.DueTime ? task.Due?.AddMinutes(-30) : null, Text: "即将截止 · " + task.Title),
                    (When: task.DueTime ? task.Due : null, Text: "截止 · " + task.Title)
                }).Where(item => item.When > from && item.When <= now).Select(item => item.Text).ToArray();
            if (messages.Length > 0)
                tray?.ShowBalloonTip(8000, "日程管理", string.Join("\n", messages.Take(4)), Forms.ToolTipIcon.Info);
        }
        catch (Exception error) { System.Diagnostics.Trace.TraceWarning(error.Message); }
    }
    private void ShowLauncher() => GetLauncher().Reveal();
    internal void RecordRecentOpen(string path, bool folder)
    {
        if (recentUsage == null) return;
        try { recentUsage.RecordOpen(path, folder); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { System.Diagnostics.Trace.TraceWarning("最近访问记录保存失败：" + error.Message); }
    }
    private void ShowHomeFromBall() => GetLauncher().RevealHome();
    private void DisplaySettingsChanged(object? sender, EventArgs args) =>
        Dispatcher.BeginInvoke(() => ball?.Place(data.Ball));

    private void ToggleLauncher()
    {
        if (launcher is { IsVisible: true, WindowState: not WindowState.Minimized })
            launcher.Conceal();
        else
            ShowLauncher();
    }

    internal bool Commit(Action<Library> edit, bool quiet = false)
    {
        if (!writable)
        {
            if (!quiet)
                Error(notice);
            return false;
        }
        var previous = LibraryStore.Copy(data);
        try
        {
            edit(data);
            store.Save(data);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            data = previous;
            notice = "保存失败。请检查程序目录权限和可用空间。";
            if (!quiet)
            {
                launcher?.RefreshData();
                Error(notice);
            }
            else if (launcher is { } window)
                _ = Dispatcher.BeginInvoke(window.RefreshData);
            return false;
        }
        if (!quiet)
            launcher?.RefreshData();
        return true;
    }

    internal async Task Refresh(bool showResult = false)
    {
        if (!writable || scanning)
            return;
        scanning = true;
        if (showResult)
        {
            scanNotice = "正在扫描应用…";
            launcher?.RefreshStatus();
        }
        try
        {
            var snapshot = await WindowsCatalog.ScanAsync();
            if (Dispatcher.HasShutdownStarted)
                return;
            if (writable)
            {
                var previous = LibraryStore.Copy(data);
                try
                {
                    if (snapshot.Merge(data))
                        store.Save(data);
                }
                catch
                {
                    data = previous;
                    throw;
                }
                launcher?.RefreshData();
                scanNotice = snapshot.Warning.Length > 0 ? snapshot.Warning :
                    showResult ? "应用扫描完成" : "";
            }
        }
        catch (Exception error)
        {
            scanNotice = "扫描未能完成。已保留现有布局。";
            System.Diagnostics.Trace.TraceWarning(error.ToString());
        }
        finally
        {
            scanning = false;
            launcher?.RefreshData();
            if (showResult && launcher is not { IsVisible: true } && scanNotice.Length > 0)
                tray?.ShowBalloonTip(3500, "AppKeeper", scanNotice, Forms.ToolTipIcon.Info);
        }
    }

    internal void Import(string path)
    {
        try
        {
            var imported = LibraryStore.Read(path);
            store.Save(imported);
            data = imported;
            ball?.Place(data.Ball);
            writable = true;
            notice = "已导入备份。";
            RegisterShortcut();
            ApplyAutostart();
            launcher?.RefreshData();
            _ = Refresh();
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            Error("导入失败。请检查备份格式和目录权限。");
        }
    }

    internal void Export(string path)
    {
        try
        {
            if (!writable)
                throw new InvalidDataException(notice);
            store.Export(path, data);
            notice = "已导出备份。";
            launcher?.RefreshStatus();
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            Error(error.Message);
        }
    }

    internal bool SaveSettings(uint modifiers, int key, bool startup, bool shellIntegration)
    {
        var previous = data;
        if ((modifiers != previous.Modifiers || key != previous.Key || !hotkeyRegistered) &&
            !TryRegisterShortcut(modifiers, key))
        {
            RegisterShortcut();
            Error("该快捷键不可用或已被占用，请换一个组合。");
            return false;
        }
        if (!Commit(library =>
        {
            library.Modifiers = modifiers;
            library.Key = key;
            library.StartWithWindows = startup;
            library.ShellIntegration = shellIntegration;
        }))
        {
            RegisterShortcut();
            return false;
        }
        hotkeyNotice = "";
        ApplyAutostart();
        ApplyShellMenu();entryTimer?.Start();
        launcher?.RefreshStatus();
        return true;
    }

    private void RegisterShortcut()
    {
        hotkeyNotice = TryRegisterShortcut(data.Modifiers, data.Key) ? "" :
            "全局快捷键不可用。请在设置中更换组合。";
        launcher?.RefreshStatus();
    }

    private bool TryRegisterShortcut(uint modifiers, int key)
    {
        if (messages == null)
            return false;
        if (hotkeyRegistered)
            UnregisterHotKey(messages.Handle, 1);
        return hotkeyRegistered = RegisterHotKey(messages.Handle, 1, modifiers | 0x4000, (uint)key);
    }

    private void ApplyAutostart()
    {
        if (profile.IsStore)
            return;
        try
        {
            var path = Environment.ProcessPath!;
            if (Path.GetFileName(path).Equals("dotnet.exe", StringComparison.OrdinalIgnoreCase))
                return;
            using var run = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
            var value = $"\"{path}\" --background";
            if (data.StartWithWindows)
            {
                if (!Equals(run.GetValue("AppKeeper"), value))
                    run.SetValue("AppKeeper", value);
                startupManaged = true;
            }
            else if (startupManaged || Equals(run.GetValue("AppKeeper"), value))
            {
                run.DeleteValue("AppKeeper", false);
                startupManaged = false;
            }
        }
        catch (Exception error) when (error is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            notice = "无法更新 Windows 登录自启动设置。";
        }
    }

    private IntPtr OnMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == 0x0312 && wParam.ToInt32() == 1)
        {
            handled = true;
            _ = Dispatcher.BeginInvoke(ToggleLauncher);
        }
        return IntPtr.Zero;
    }

    internal void Error(string text)
    {
        if (launcher is { IsVisible: true })
            MessageBox.Show(launcher, text, "AppKeeper", MessageBoxButton.OK, MessageBoxImage.Warning);
        else
            MessageBox.Show(text, "AppKeeper", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        entryTimer?.Stop();
        reminderTimer?.Stop();
        agendaSync?.Dispose();
        recentUsage?.Dispose();
        if (pigeon != null)
        {
            pigeon.UnreadChanged -= PigeonUnreadChanged;
            pigeon.Dispose();
        }
        SystemEvents.DisplaySettingsChanged -= DisplaySettingsChanged;
        scanTimer?.Stop();
        showWait?.Unregister(null);
        showEvent?.Dispose();
        instance?.Dispose();
        if (messages != null && hotkeyRegistered)
            UnregisterHotKey(messages.Handle, 1);
        messages?.Dispose();
        if (tray != null)
        {
            tray.Visible = false;
            tray.ContextMenuStrip?.Dispose();
            tray.Dispose();
        }
        trayIcon?.Dispose();
        base.OnExit(e);
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint key);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(IntPtr window, int id);
}
