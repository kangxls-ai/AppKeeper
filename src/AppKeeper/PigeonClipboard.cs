using System;
using System.Collections.Specialized;
using System.IO;
using System.Windows;

namespace AppKeeper;

internal static class PigeonClipboard
{
    internal static DataObject CreateData(PigeonItem item)
    {
        var data = new DataObject();
        if (item.Kind == "text")
            data.SetText(item.Value, TextDataFormat.UnicodeText);
        else
        {
            var path = item.LocalFile ?? throw new InvalidOperationException("文件接收完成后才能复制。");
            if (!File.Exists(path))
                throw new FileNotFoundException("找不到此文件，可能已移动或删除。", path);
            data.SetFileDropList(new StringCollection { path });
        }
        return data;
    }

    internal static void Copy(PigeonItem item) => Clipboard.SetDataObject(CreateData(item), true);
}
