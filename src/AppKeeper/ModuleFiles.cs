using System;
using System.IO;
using System.Text.Json;

namespace AppKeeper;

internal static class ModuleFiles
{
    public static T Read<T>(string path) where T : new()
    {
        if (!File.Exists(path))
            return new();
        return JsonSerializer.Deserialize<T>(File.ReadAllBytes(path)) ??
            throw new InvalidDataException("数据文件为空。");
    }

    public static void Write<T>(string path, T data)
    {
        var temp = path + ".new";
        try
        {
            using (var file = new FileStream(temp, FileMode.Create))
            {
                JsonSerializer.Serialize(file, data);
                file.Flush(true);
            }
            File.Move(temp, path, true);
        }
        finally { File.Delete(temp); }
    }
}
