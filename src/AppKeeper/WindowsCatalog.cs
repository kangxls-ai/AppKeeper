using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace AppKeeper;

internal static class WindowsCatalog
{
    public static bool IsWeb(string target) => Uri.TryCreate(target, UriKind.Absolute, out var uri) &&
        uri.Scheme is "https" or "http" && !string.IsNullOrEmpty(uri.Host);

    public static bool IsAppId(string target) =>
        target.Contains('!') && target.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_' or '!');

    public static Task<CatalogSnapshot> ScanAsync()
    {
        var completion = new TaskCompletionSource<CatalogSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new System.Threading.Thread(() =>
        {
            try { completion.SetResult(Scan()); }
            catch (Exception e) { completion.SetException(e); }
        }) { IsBackground = true };
        thread.SetApartmentState(System.Threading.ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

    private static CatalogSnapshot Scan()
    {
        var apps = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        var warnings = new List<string>();
        dynamic? shortcuts = null;
        try
        {
            shortcuts = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell", true)!);
            foreach (var folder in new[] { Environment.SpecialFolder.Programs, Environment.SpecialFolder.CommonPrograms })
            {
                var path = Environment.GetFolderPath(folder);
                if (string.IsNullOrEmpty(path) || !Directory.Exists(path))
                    continue;

                var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true };
                foreach (var file in Directory.EnumerateFiles(path, "*", options))
                {
                    try
                    {
                        var entry = ReadShortcut(shortcuts!, file);
                        if (entry != null)
                            apps.TryAdd(entry.Source!, entry);
                    }
                    catch (Exception e) when (e is IOException or COMException or UnauthorizedAccessException)
                    {
                        // One stale shortcut should not prevent discovery of the remaining shortcuts.
                    }
                }
            }
        }
        catch (Exception e) when (e is IOException or COMException or UnauthorizedAccessException)
        {
            warnings.Add("部分开始菜单入口无法读取");
        }
        finally { Release(shortcuts); }

        foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            try
            {
                using var root = RegistryKey.OpenBaseKey(hive, view);
                using var paths = root.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\App Paths");
                if (paths == null)
                    continue;
                foreach (var name in paths.GetSubKeyNames())
                {
                    using var key = paths.OpenSubKey(name);
                    if (key?.GetValue("") is not string raw)
                        continue;
                    var path = Environment.ExpandEnvironmentVariables(raw.Trim().Trim('"'));
                    if (!File.Exists(path) || !Path.GetExtension(path).Equals(".exe", StringComparison.OrdinalIgnoreCase))
                        continue;
                    var product = FileVersionInfo.GetVersionInfo(path).FileDescription;
                    var entry = Discovered(EntryKind.File,
                        string.IsNullOrWhiteSpace(product) ? Path.GetFileNameWithoutExtension(name) : product,
                        path, FileId(path, ""));
                    apps.TryAdd(entry.Source!, entry);
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                warnings.Add("部分 Win32 注册入口无法读取");
            }
        }

        var storeComplete = ReadAppsFolder(apps);
        if (!storeComplete)
            warnings.Add("Microsoft Store 应用列表无法完整读取");
        return new(apps, storeComplete, string.Join("；", warnings.Distinct()));
    }

    private static Entry? ReadShortcut(dynamic shell, string file)
    {
        var ext = Path.GetExtension(file);
        var name = Path.GetFileNameWithoutExtension(file);
        if (ext.Equals(".exe", StringComparison.OrdinalIgnoreCase))
            return Discovered(EntryKind.File, name, file, FileId(file, ""));
        if (!ext.Equals(".lnk", StringComparison.OrdinalIgnoreCase))
            return null;

        dynamic? link = null;
        try
        {
            link = shell.CreateShortcut(file);
            string path = Environment.ExpandEnvironmentVariables((string)link!.TargetPath);
            string args = link.Arguments;
            if (path.StartsWith(@"shell:AppsFolder\", StringComparison.OrdinalIgnoreCase))
                return null;
            if (!string.IsNullOrEmpty(path) && (!Path.IsPathFullyQualified(path) ||
                !Path.GetExtension(path).Equals(".exe", StringComparison.OrdinalIgnoreCase) || !File.Exists(path)))
                return null;
            return Discovered(EntryKind.File, name, file,
                path.Length == 0 ? "shortcut:" + file : FileId(path, args));
        }
        finally { Release(link); }
    }

    private static bool ReadAppsFolder(Dictionary<string, Entry> apps)
    {
        dynamic? shell = null;
        dynamic? folder = null;
        dynamic? items = null;
        try
        {
            shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application", true)!);
            folder = shell!.NameSpace("shell:AppsFolder");
            if (folder == null)
                return false;
            items = folder.Items();
            int count = items!.Count;
            for (var i = 0; i < count; i++)
            {
                dynamic? item = null;
                try
                {
                    item = items.Item(i);
                    string id = item!.Path;
                    string name = item.Name;
                    if (IsAppId(id) && !string.IsNullOrWhiteSpace(name))
                        apps.TryAdd("store:" + id, Discovered(EntryKind.Store, name, id, "store:" + id));
                }
                finally { Release(item); }
            }
            return true;
        }
        catch (COMException) { return false; }
        finally
        {
            Release(items);
            Release(folder);
            Release(shell);
        }
    }

    private static string FileId(string path, string args) => $"win32:{path}|{args}";

    private static Entry Discovered(EntryKind kind, string name, string target, string source) =>
        new() { Kind = kind, Name = name, DefaultName = name, Target = target, Source = source, Inbox = true };

    private static void Release(object? instance)
    {
        if (instance != null && Marshal.IsComObject(instance))
            Marshal.ReleaseComObject(instance);
    }

    public static Entry FromPath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var isDirectory = Directory.Exists(fullPath);
        if (!isDirectory && !File.Exists(fullPath))
            throw new FileNotFoundException("找不到所选文件或文件夹。", fullPath);
        var name = isDirectory ? Path.GetFileName(Path.TrimEndingDirectorySeparator(fullPath)) :
            Path.GetFileNameWithoutExtension(fullPath);
        return new() { Name = string.IsNullOrWhiteSpace(name) ? fullPath : name,
            Target = fullPath, Kind = isDirectory ? EntryKind.Directory : EntryKind.File };
    }

    public static Entry FromUrl(string input)
    {
        var value = input.Trim();
        if (!value.Contains("://", StringComparison.Ordinal))
            value = "https://" + value;
        if (!IsWeb(value))
            throw new ArgumentException("请输入有效的 HTTP 或 HTTPS 网址。");
        return new() { Kind = EntryKind.Web, Name = new Uri(value).Host, Target = value };
    }

    public static bool IsMissing(Entry entry) => entry.Kind switch
    {
        EntryKind.File => !File.Exists(entry.Target) ||
            (entry.Source?.StartsWith("win32:", StringComparison.OrdinalIgnoreCase) == true &&
             !File.Exists(entry.Source[6..].Split('|', 2)[0])),
        EntryKind.Directory => !Directory.Exists(entry.Target),
        _ => entry.Missing
    };

    public static void Launch(Entry entry)
    {
        if (IsMissing(entry))
            throw new InvalidOperationException("该入口已不可用。");
        if (entry.Kind == EntryKind.Store)
        {
            var manager = (IApplicationActivationManager)new ApplicationActivationManager();
            try { Marshal.ThrowExceptionForHR(manager.ActivateApplication(entry.Target, null, 0, out _)); }
            finally { Marshal.ReleaseComObject(manager); }
            return;
        }
        using var process = Process.Start(new ProcessStartInfo(entry.Target) { UseShellExecute = true });
    }

    [ComImport, Guid("45BA127D-10A8-46EA-8AB7-56EA9078943C")]
    private class ApplicationActivationManager { }

    [ComImport, Guid("2e941141-7f97-4756-ba1d-9decde894a3d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IApplicationActivationManager
    {
        [PreserveSig]
        int ActivateApplication([MarshalAs(UnmanagedType.LPWStr)] string appId,
            [MarshalAs(UnmanagedType.LPWStr)] string? arguments, uint options, out uint processId);
    }
}

internal sealed record CatalogSnapshot(Dictionary<string, Entry> Apps, bool StoreComplete, string Warning)
{
    public bool Merge(Library data)
    {
        var changed = false;
        var remaining = new Dictionary<string, Entry>(Apps, StringComparer.OrdinalIgnoreCase);
        var byTarget = Apps.Values.ToLookup(entry => (entry.Kind, entry.Target.ToUpperInvariant()));
        foreach (var entry in data.Entries)
        {
            Entry? discovered = null;
            if (entry.Source != null)
            {
                if (!remaining.Remove(entry.Source, out discovered))
                {
                    discovered = byTarget[(entry.Kind, entry.Target.ToUpperInvariant())]
                        .FirstOrDefault(candidate => remaining.ContainsKey(candidate.Source!));
                    if (discovered != null)
                        remaining.Remove(discovered.Source!);
                }
            }
            else if (entry.Kind is EntryKind.File or EntryKind.Directory)
            {
                foreach (var candidate in byTarget[(entry.Kind, entry.Target.ToUpperInvariant())])
                    remaining.Remove(candidate.Source!);
            }

            var missing = entry.Kind == EntryKind.Store
                ? StoreComplete ? discovered == null : entry.Missing
                : discovered == null && WindowsCatalog.IsMissing(entry);
            if (entry.Missing != missing)
            {
                entry.Missing = missing;
                changed = true;
            }
            if (discovered != null)
            {
                if (entry.Name == entry.DefaultName && entry.Name != discovered.Name)
                {
                    entry.Name = discovered.Name;
                    changed = true;
                }
                if (entry.Target != discovered.Target || entry.DefaultName != discovered.Name || entry.Source != discovered.Source)
                {
                    entry.Target = discovered.Target;
                    entry.DefaultName = discovered.Name;
                    entry.Source = discovered.Source;
                    changed = true;
                }
            }
        }
        foreach (var entry in remaining.Values.OrderBy(entry => entry.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            if (data.Ignored.Contains(entry.Source!) || data.Ignored.Contains("target:" + entry.Target))
                continue;
            data.Entries.Add(new Entry
            {
                Kind = entry.Kind, Name = entry.Name, DefaultName = entry.DefaultName,
                Target = entry.Target, Source = entry.Source, Inbox = true
            });
            changed = true;
        }
        return changed;
    }
}
