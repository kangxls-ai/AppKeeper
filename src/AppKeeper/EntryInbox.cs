using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AppKeeper;

// A durable per-installation inbox: secondary processes never write the library itself.
internal sealed class EntryInbox(string root)
{
    internal const int Limit = 1000;
    internal string DirectoryPath => Path.Combine(root, "entry-inbox");
    internal static string Scope(string root) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
        OperatingSystem.IsWindows() ? Path.GetFullPath(root).TrimEnd('\\').ToUpperInvariant() : Path.GetFullPath(root))))[..16];
    internal static string[] Arguments(string[] args) => args.Length > 0 && args[0] == "--add" ? args.Skip(1).ToArray() : [];
    internal static string[] Normalize(IEnumerable<string> paths) => paths.Select(path =>
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)) throw new ArgumentException("请选择本机文件、文件夹或应用。");
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }).Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal).ToArray();
    internal void Enqueue(IEnumerable<string> paths)
    {
        var values = Normalize(paths);if(values.Length == 0 || values.Length > Limit)throw new ArgumentException("每次请选择 1 至 1000 个入口。");
        Directory.CreateDirectory(DirectoryPath);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(DirectoryPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var file = Path.Combine(DirectoryPath, Guid.NewGuid().ToString("N") + ".json");
        var temp = file + ".tmp";
        try
        {
            var bytes=JsonSerializer.SerializeToUtf8Bytes(new Request(values));
            if(bytes.Length>1024*1024)throw new ArgumentException("所选路径过多或过长，请分批添加。");
            File.WriteAllBytes(temp,bytes);File.Move(temp,file);
        }
        finally { if(File.Exists(temp))File.Delete(temp); }
    }
    internal sealed record Request(string[] Paths);
    internal sealed record Batch(string[] Files,string[] Paths);
    internal Batch Read()
    {
        if(!Directory.Exists(DirectoryPath))return new([],[]);
        var files=new List<string>();var paths=new List<string>();
        foreach(var file in Directory.EnumerateFiles(DirectoryPath,"*.json").OrderBy(File.GetLastWriteTimeUtc).Take(1000))
        {
            try
            {
                if(new FileInfo(file).Length > 1024*1024)throw new InvalidDataException();
                var request=JsonSerializer.Deserialize<Request>(File.ReadAllText(file));
                if(request?.Paths == null || request.Paths.Length is 0 or > Limit)throw new InvalidDataException();
                var values=Normalize(request.Paths);
                if(paths.Count+values.Length>Limit)break;
                files.Add(file);paths.AddRange(values);
            }
            catch(Exception e) when(e is JsonException or InvalidDataException or ArgumentException)
            { File.Move(file,file+".invalid",true); }
        }
        return new(files.ToArray(),Normalize(paths));
    }
    // Acknowledge only after Save succeeds or the user explicitly cancels. Crashes preserve requests.
    internal void Complete(Batch batch){foreach(var file in batch.Files)if(File.Exists(file))File.Delete(file);}
}
