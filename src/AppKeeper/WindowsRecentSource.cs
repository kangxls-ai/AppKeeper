using Microsoft.Win32;
using Microsoft.CSharp.RuntimeBinder;
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;

namespace AppKeeper;

internal sealed class WindowsRecentSource
{
    private readonly Dictionary<string, (long Stamp, long Length, RecentObservation Item)> links = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<long, string> windows = [];
    private bool initialized;
    internal string Warning { get; private set; } = "";

    internal void Reset() { links.Clear(); windows.Clear(); initialized = false; }

    internal static string? DisabledReason()
    {
        try
        {
            foreach (var root in new[] { Registry.CurrentUser, Registry.LocalMachine })
            {
                using var policy = root.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Policies\Explorer");
                if (policy?.GetValue("NoRecentDocsHistory") is int history && history == 1)
                    return "Windows 策略禁止保存最近文档，系统采集已暂停。";
            }
            using var settings = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced");
            if (settings?.GetValue("Start_TrackDocs") is int enabled && enabled == 0)
                return "Windows 最近项目未开启，系统采集已暂停。可在“系统设置”中查看。";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return "无法读取 Windows 最近项目设置，系统采集已暂停。";
        }
        return null;
    }

    internal RecentObservation[] Read(DateTimeOffset now, string? recentDirectory = null, bool explorer = true)
    {
        var result = new List<RecentObservation>();
        var warnings = new List<string>();
        var root = recentDirectory ?? Environment.GetFolderPath(Environment.SpecialFolder.Recent);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (Directory.Exists(root))
            {
                foreach (var file in Directory.EnumerateFiles(root, "*.lnk", SearchOption.TopDirectoryOnly))
                {
                    seen.Add(file);
                    try
                    {
                        var info = new FileInfo(file);
                        if (links.TryGetValue(file, out var cached) && cached.Stamp == info.LastWriteTimeUtc.Ticks && cached.Length == info.Length)
                        {
                            result.Add(cached.Item with { Count = false });
                            continue;
                        }
                        var item = ReadLink(file, now, initialized);
                        if (item != null)
                        {
                            links[file] = (info.LastWriteTimeUtc.Ticks, info.Length, item);
                            result.Add(item);
                        }
                    }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException or COMException or ArgumentException or RuntimeBinderException)
                    { /* A stale or half-written shortcut must not block the remaining history. */ }
                }
                foreach (var stale in links.Keys.Where(path => !seen.Contains(path)).ToArray()) links.Remove(stale);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or COMException)
        {
            warnings.Add("部分系统最近记录读取失败");
        }
        if (explorer)
        {
            try { result.AddRange(ReadExplorer(now)); }
            catch (Exception error) when (error is COMException or IOException or UnauthorizedAccessException or RuntimeBinderException)
            { warnings.Add("资源管理器文件夹暂不可读取"); }
        }
        initialized = true;
        Warning = string.Join("；", warnings);
        return result.ToArray();
    }

    private static RecentObservation? ReadLink(string file, DateTimeOffset now, bool count)
    {
        // Read the stored target attributes, never probe a disconnected UNC target to determine its type.
        Span<byte> header = stackalloc byte[28];
        using (var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            input.ReadExactly(header);
        if (BinaryPrimitives.ReadUInt32LittleEndian(header) != 76) return null;
        var folder = (BinaryPrimitives.ReadUInt32LittleEndian(header[24..]) & 0x10) != 0;
        var path = RecentUsageStore.Normalize(RecentShortcut.ReadTarget(file));
        if (path == null) return null;
        var at = new DateTimeOffset(File.GetLastWriteTimeUtc(file));
        if (at > now) at = now;
        return new(path, folder, at, count, "Windows 最近项目");
    }

    private IEnumerable<RecentObservation> ReadExplorer(DateTimeOffset now)
    {
        var result = new List<RecentObservation>();
        var current = new Dictionary<long, string>();
        dynamic? shell = null;
        dynamic? collection = null;
        try
        {
            shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application", true)!);
            collection = shell!.Windows();
            int count = collection.Count;
            for (var i = 0; i < count; i++)
            {
                dynamic? window = null;
                try
                {
                    window = collection.Item(i);
                    if (window == null) continue;
                    if (!string.Equals(Path.GetFileName((string)window.FullName), "explorer.exe", StringComparison.OrdinalIgnoreCase)) continue;
                    long id = window!.HWND;
                    string location = window.LocationURL;
                    if (!Uri.TryCreate(location, UriKind.Absolute, out var uri) || !uri.IsFile) continue;
                    var path = RecentUsageStore.Normalize(uri.LocalPath);
                    if (path == null) continue;
                    current[id] = path;
                }
                catch (Exception error) when (error is COMException or ArgumentException or InvalidCastException or RuntimeBinderException)
                { /* Windows may close or navigate while being enumerated. */ }
                finally { Release(window); }
            }
            foreach (var (id, path) in current)
                if (initialized && (!windows.TryGetValue(id, out var previous) || !path.Equals(previous, StringComparison.OrdinalIgnoreCase)))
                    result.Add(new(path, true, now, true, "资源管理器访问"));
            windows = current;
        }
        finally { Release(collection); Release(shell); }
        return result;
    }

    private static void Release(object? value)
    {
        if (value != null && Marshal.IsComObject(value)) Marshal.ReleaseComObject(value);
    }
}

internal sealed class RecentUsageService : IDisposable
{
    internal RecentUsageStore Store { get; }
    private readonly AutoResetEvent wake = new(false);
    private readonly bool observing;
    private volatile bool stopping;
    private volatile string status = "正在读取系统最近记录…";
    internal string Status => status;

    internal RecentUsageService(string directory, bool observeWindows = true)
    {
        Store = new(directory);
        observing = observeWindows;
        if (!observing) { status = "本机记录已暂停"; return; }
        var thread = new Thread(Observe) { IsBackground = true, Name = "AppKeeper recent activity" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }

    private void Observe()
    {
        var source = new WindowsRecentSource();
        RecentObservation[] pending = [];
        try
        {
            while (!stopping)
            {
                try
                {
                    var enabled = Store.Snapshot().Enabled;
                    var disabled = WindowsRecentSource.DisabledReason();
                    if (!enabled || disabled != null)
                    {
                        source.Reset();
                        pending = [];
                        status = !enabled ? "本机记录已暂停，已有记录仍可查看。" : disabled!;
                    }
                    else
                    {
                        var now = DateTimeOffset.UtcNow;
                        pending = pending.Concat(source.Read(now)).ToArray();
                        if (stopping) break;
                        Store.Observe(pending, now);
                        pending = [];
                        status = source.Warning.Length > 0 ? source.Warning : "系统记录可用 · 每 3 秒更新，快速跳转可能漏记";
                    }
                }
                catch (Exception error)
                {
                    System.Diagnostics.Trace.TraceWarning(error.ToString());
                    status = "最近记录更新失败，请检查数据目录权限或稍后重试。";
                }
                if (!stopping) wake.WaitOne(TimeSpan.FromSeconds(3));
            }
        }
        finally { wake.Dispose(); }
    }

    internal void Refresh() { if (!stopping) wake.Set(); }
    internal void RecordOpen(string path, bool folder)
    {
        var now = DateTimeOffset.UtcNow;
        Store.Observe([new(path, folder, now, true, "AppKeeper 打开")], now);
        Refresh();
    }
    public void Dispose()
    {
        if (stopping) return;
        stopping = true;
        if (observing) wake.Set(); else wake.Dispose();
    }
}
