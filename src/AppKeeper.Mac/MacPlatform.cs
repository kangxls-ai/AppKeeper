using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AppKeeper;

internal static class MacPlatform
{
    internal static string Root => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Application Support", "EasyAppKeeper");
    internal static string Helper => Path.Combine(AppContext.BaseDirectory, "EasyAppKeeper.Native");
    internal static async Task<string> Native(params string[] args)
    {
        var start = new ProcessStartInfo(Helper) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new IOException("无法启动 macOS 系统组件。");
        var output = process.StandardOutput.ReadToEndAsync(); var errors = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync(); var result = await output; var error = await errors;
        if (process.ExitCode != 0) throw new IOException(string.IsNullOrWhiteSpace(error) ? "macOS 操作失败。" : error.Trim());
        return result.Trim();
    }
    internal static void Open(string path, bool reveal = false)
    {
        var start = new ProcessStartInfo("/usr/bin/open") { UseShellExecute = false };
        if (reveal) start.ArgumentList.Add("-R");
        start.ArgumentList.Add(path); Process.Start(start)?.Dispose();
    }
    [System.Runtime.CompilerServices.ModuleInitializer]
    internal static void Initialize() => NativeLibrary.SetDllImportResolver(typeof(MacPlatform).Assembly, (name, assembly, paths) =>
        name != "winsqlite3.dll" ? IntPtr.Zero : OperatingSystem.IsMacOS() ? NativeLibrary.Load("/usr/lib/libsqlite3.dylib") : OperatingSystem.IsLinux() ? NativeLibrary.Load("libsqlite3.so.0") : IntPtr.Zero);
}

// Library validation uses this name in the shared Windows source; no Win32 APIs are called here.
internal static class WindowsCatalog
{
    internal static bool IsWeb(string value) => Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http";
    internal static bool IsAppId(string value) => !string.IsNullOrWhiteSpace(value);
}

internal static class AgendaKeyProtection
{
    private static byte[]? key;
    private static byte[] Key()
    {
        if (key != null) return key;
        var testKey = Environment.GetEnvironmentVariable("APPKEEPER_MAC_TEST_KEY");
        var value = OperatingSystem.IsMacOS() ? MacPlatform.Native("--key").GetAwaiter().GetResult() : testKey ?? throw new PlatformNotSupportedException("Mac 配对凭据需要 macOS 钥匙串。");
        return key = Convert.FromBase64String(value);
    }
    internal static string Protect(string value)
    {
        var plain = Encoding.UTF8.GetBytes(value); var data = new byte[28 + plain.Length]; RandomNumberGenerator.Fill(data.AsSpan(0,12));
        using var aes = new AesGcm(Key(),16); aes.Encrypt(data.AsSpan(0,12), plain, data.AsSpan(28), data.AsSpan(12,16));
        return Convert.ToBase64String(data);
    }
    internal static string Unprotect(string value)
    {
        var data = Convert.FromBase64String(value); if(data.Length < 28) throw new CryptographicException("配对凭据损坏。");
        var plain = new byte[data.Length - 28]; using var aes = new AesGcm(Key(),16);
        aes.Decrypt(data.AsSpan(0,12),data.AsSpan(28),data.AsSpan(12,16),plain); return Encoding.UTF8.GetString(plain);
    }
}

internal sealed record MacVisit(string Path, string Name, EntryKind Kind, DateTimeOffset At);
internal sealed class MacSettings
{
    public bool Ball { get; set; } = true;
    public bool RecordVisits { get; set; } = true;
    public uint HotkeyCode { get; set; } = 49;
    public uint HotkeyModifiers { get; set; } = 2048;
    public int BallX { get; set; } = 100;
    public int BallY { get; set; } = 100;
    public bool HasBallPosition { get; set; }
    public List<MacVisit> Visits { get; set; } = [];
    public List<string> Excluded { get; set; } = [];
    public List<string> Hidden { get; set; } = [];
}

internal sealed class MacState
{
    internal string Root { get; }
    internal LibraryStore Store { get; }
    internal Library Data { get; private set; }
    internal MacSettings Settings { get; }
    internal MacState(string root)
    {
        Root = root; Directory.CreateDirectory(root);
        if (OperatingSystem.IsMacOS()) File.SetUnixFileMode(root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        Store = new(root); Data = Store.Load(out _); Layout.EnsureTagOrders(Data);
        Settings = ReadSettings();
    }
    private MacSettings ReadSettings()
    {
        var file = Path.Combine(Root,"EasyAppKeeper.mac.json");
        try { return ModuleFiles.Read<MacSettings>(file); }
        catch(Exception e) when (e is IOException or JsonException) { return ModuleFiles.Read<MacSettings>(file+".bak"); }
    }
    internal void Save()
    {
        Store.Save(Data); var file = Path.Combine(Root,"EasyAppKeeper.mac.json");
        if(File.Exists(file)) File.Copy(file,file+".bak",true);
        ModuleFiles.Write(file,Settings);
    }
    internal void Import(string path) { var next = Library.Read(path); Data = next; Save(); }
    internal void Add(string path, string? tag, string? label = null)
    {
        var kind = WindowsCatalog.IsWeb(path) ? EntryKind.Web : Directory.Exists(path) && !path.EndsWith(".app",StringComparison.OrdinalIgnoreCase) ? EntryKind.Directory : EntryKind.File;
        var existing = Data.Entries.Find(e=>e.Target==path);
        if(existing!=null) { Layout.AssignTag(Data,existing,tag); Save(); return; }
        var entry = new Entry { Name=label??Path.GetFileNameWithoutExtension(path),Target=path,Kind=kind };
        if(kind==EntryKind.Web && label==null) entry.Name=new Uri(path).Host;
        Data.Entries.Add(entry); Layout.AssignTag(Data,entry,tag); Save();
    }
    internal void Visit(Entry entry)
    {
        QuickHistory.RecordVisit(Data,entry.Id);
        if(Settings.RecordVisits && !Settings.Excluded.Any(p=>entry.Target==p || entry.Target.StartsWith(p+Path.DirectorySeparatorChar,StringComparison.Ordinal)))
        {
            Settings.Hidden.Remove(entry.Target); Settings.Visits.Add(new(entry.Target,entry.Name,entry.Kind,DateTimeOffset.Now));
            Settings.Visits = Settings.Visits.Where(v=>v.At>DateTimeOffset.Now.AddDays(-90)).TakeLast(20000).ToList();
        }
        Save();
    }
}
