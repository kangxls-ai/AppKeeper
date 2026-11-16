using System;
using System.IO;
using System.Linq;

namespace AppKeeper;

internal static class PortableMigration
{
    internal static void Run(string source, string destination)
    {
        source = Path.GetFullPath(source);
        if (!File.Exists(Path.Combine(source, "AppKeeper.exe")))
            throw new InvalidDataException("请选择包含 AppKeeper.exe 的绿色版目录。");
        if (Directory.Exists(destination))
            throw new InvalidOperationException("商店版已有数据。可在设置中导入布局备份。");
        var from = new LibraryStore(source);
        if (!File.Exists(from.DataPath) && !File.Exists(from.BackupPath))
            throw new FileNotFoundException("绿色版目录中找不到 AppKeeper 的配置或备份。");
        var data = from.Load(out _);
        // The package startup task is controlled separately by Windows.
        data.StartWithWindows = false;

        var stage = destination + ".migration-" + Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(stage);
        try
        {
            new LibraryStore(stage).Save(data);
            MigrateAgenda(source, stage);
            MigratePigeon(source, stage, destination);
            Directory.Move(stage, destination);
        }
        finally
        {
            if (Directory.Exists(stage))
                Directory.Delete(stage, recursive: true);
        }
    }

    private static void MigrateAgenda(string source, string stage)
    {
        const string settings = "AppKeeper.agenda.json";
        const string database = "AppKeeper.agenda.db";
        var config = Path.Combine(source, settings);
        if (File.Exists(config))
        {
            var agenda = ModuleFiles.Read<AgendaSettings>(config);
            if (agenda.Path != null)
                agenda.Path = Path.GetFullPath(agenda.Path, source);
            ModuleFiles.Write(Path.Combine(stage, settings), agenda);
        }

        var databasePath = Path.Combine(source, database);
        if (File.Exists(databasePath))
        {
            using var db = Sqlite.Open(databasePath, readOnly: true);
            using var copy = Sqlite.Open(Path.Combine(stage, database), create: true);
            db.BackupTo(copy);
        }
    }

    private static void MigratePigeon(string source, string stage, string destination)
    {
        const string settings = "AppKeeper.pigeon.json";
        const string certificate = "AppKeeper.pigeon.pfx";
        const string transfer = "pigeon-transfer";
        var config = Path.Combine(source, settings);
        var key = Path.Combine(source, certificate);
        if (File.Exists(key))
        {
            using var identity = PigeonWire.Identity(key);
            File.Copy(key, Path.Combine(stage, certificate));
        }
        if (!File.Exists(config))
            return;

        var pigeon = ModuleFiles.Read<PigeonData>(config);
        if (pigeon.Peers == null || pigeon.Items == null || string.IsNullOrWhiteSpace(pigeon.Share))
            throw new InvalidDataException("绿色版飞鸽传书配置格式无效。");
        if (!File.Exists(key) && pigeon.Peers.Count > 0)
            throw new InvalidDataException("绿色版飞鸽传书的配对标识缺失。");
        pigeon.Items = pigeon.Items.Select(item =>
        {
            if (!item.Outgoing || item.Kind != "file" || item.Path == null)
                return item;
            var name = item.Id.ToString("N") + ".zip";
            var archive = Path.Combine(source, transfer, name);
            if (!archive.Equals(item.Path, StringComparison.OrdinalIgnoreCase) || !File.Exists(archive))
                return item;
            Directory.CreateDirectory(Path.Combine(stage, transfer));
            File.Copy(archive, Path.Combine(stage, transfer, name));
            return item with { Path = Path.Combine(destination, transfer, name),
                SourceModified = File.GetLastWriteTimeUtc(archive) };
        }).ToList();
        ModuleFiles.Write(Path.Combine(stage, settings), pigeon);
    }
}
