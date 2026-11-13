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
    private readonly LibraryStore store = new(AppContext.BaseDirectory);
    private Mutex? instance;
    private EventWaitHandle? showEvent;
    private RegisteredWaitHandle? showWait;
    private HwndSource? messages;
    private Forms.NotifyIcon? tray;
    private System.Drawing.Icon? trayIcon;
    private DispatcherTimer? scanTimer;
    private MainWindow? launcher;
    private Library data = new();
    private bool writable = true;
    private bool scanning;
    private bool hotkeyRegistered;
    private bool startupManaged;
    private string notice = "";
    private string scanNotice = "";
    private string hotkeyNotice = "";

    internal Library Data => data;
    internal string Status => string.Join("　", new[] { notice, scanNotice, hotkeyNotice }.Where(text => text.Length > 0));

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (e.Args.Contains("--self-test"))
        {
            Shutdown(SelfTest.Run());
            return;
        }

        const string instanceName = @"Local\AppKeeper.V1";
        using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        var name = instanceName + "." + identity.User?.Value;
        showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, name + ".Show");
        instance = new Mutex(false, name, out var first);
        if (!first)
        {
            if (!e.Args.Contains("--background"))
                showEvent.Set();
            Shutdown();
            return;
        }

        try
        {
            data = store.Load(out var recovery);
            notice = recovery ?? "";
            if (recovery != null || !File.Exists(store.DataPath))
                store.Save(data);
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            writable = false;
            notice = error is InvalidDataException ? error.Message :
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
            ApplyAutostart();

        showWait = ThreadPool.RegisterWaitForSingleObject(showEvent, (_, _) =>
        {
            if (!Dispatcher.HasShutdownStarted)
                _ = Dispatcher.BeginInvoke(ShowLauncher);
        }, null, Timeout.Infinite, false);

        if (!e.Args.Contains("--background"))
            ShowLauncher();
        else if (Status.Length > 0)
            tray!.ShowBalloonTip(5000, "AppKeeper", Status, Forms.ToolTipIcon.Warning);

        if (writable)
            _ = Refresh();
        scanTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(10) };
        scanTimer.Tick += async (_, _) => await Refresh();
        scanTimer.Start();
    }

    private void CreateTray()
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("显示 AppKeeper", null, (_, _) => ShowLauncher());
        menu.Items.Add("设置", null, (_, _) => GetLauncher().ShowSettingsFromTray());
        menu.Items.Add("重新扫描应用", null, async (_, _) => await Refresh(showResult: true));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("退出 AppKeeper", null, (_, _) => Shutdown());
        using var stream = GetResourceStream(new Uri("Assets/AppKeeper.ico", UriKind.Relative)).Stream;
        trayIcon = new System.Drawing.Icon(stream);
        tray = new Forms.NotifyIcon { Icon = trayIcon, Text = "AppKeeper", ContextMenuStrip = menu, Visible = true };
        tray.MouseClick += (_, args) =>
        {
            if (args.Button == Forms.MouseButtons.Left)
                ToggleLauncher();
        };
    }

    private MainWindow GetLauncher() => launcher ??= new MainWindow(this);
    private void ShowLauncher() => GetLauncher().Reveal();

    private void ToggleLauncher()
    {
        if (launcher is { IsVisible: true })
            launcher.Conceal();
        else
            ShowLauncher();
    }

    internal bool Commit(Action<Library> edit)
    {
        if (!writable)
        {
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
            launcher?.RefreshData();
            Error(notice);
            return false;
        }
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
                var next = LibraryStore.Copy(data);
                if (snapshot.Merge(next))
                {
                    store.Save(next);
                    data = next;
                    launcher?.RefreshData();
                }
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
            launcher?.RefreshStatus();
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

    internal bool SaveSettings(uint modifiers, int key, bool startup)
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
        }))
        {
            RegisterShortcut();
            return false;
        }
        hotkeyNotice = "";
        ApplyAutostart();
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
        else if (message is 0x007E or 0x02E0)
        {
            _ = Dispatcher.BeginInvoke(() => launcher?.FitMonitor());
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
