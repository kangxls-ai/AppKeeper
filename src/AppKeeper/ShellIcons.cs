using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AppKeeper;

internal sealed class ShellIcons
{
    private readonly Dictionary<string, ImageSource?> cache = new(StringComparer.OrdinalIgnoreCase);
    public void Clear() => cache.Clear();

    public ImageSource? Get(Entry entry)
    {
        var key = entry.Icon ?? $"{entry.Kind}:{entry.Target}:{entry.Missing}";
        if (cache.TryGetValue(key, out var existing))
            return existing;
        ImageSource? image = null;
        try
        {
            if (entry.Icon != null)
                image = Decode(Convert.FromBase64String(entry.Icon), 96);
            else if (entry.Kind is EntryKind.File or EntryKind.Directory or EntryKind.Store && !entry.Missing)
                image = ReadShell(entry.Kind == EntryKind.Store ? @"shell:AppsFolder\" + entry.Target : entry.Target);
        }
        catch (Exception e) when (e is IOException or FormatException or ArgumentException or NotSupportedException or COMException) { }
        return cache[key] = image;
    }

    public static string ReadCustom(string file)
    {
        BitmapSource bitmap;
        if (Path.GetExtension(file).Equals(".exe", StringComparison.OrdinalIgnoreCase) ||
            Path.GetExtension(file).Equals(".lnk", StringComparison.OrdinalIgnoreCase))
        {
            bitmap = ReadShell(file) ?? throw new InvalidDataException("无法从该文件提取图标。");
        }
        else
        {
            if (new FileInfo(file).Length > 16 * 1024 * 1024)
                throw new InvalidDataException("图标文件超过 16 MB。");
            bitmap = Decode(File.ReadAllBytes(file), 96);
        }
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return Convert.ToBase64String(stream.ToArray());
    }

    private static BitmapSource Decode(byte[] bytes, int width)
    {
        using var stream = new MemoryStream(bytes);
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.DecodePixelWidth = width;
        bitmap.StreamSource = stream;
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }

    private static BitmapSource? ReadShell(string path)
    {
        if (SHParseDisplayName(path, IntPtr.Zero, out var pidl, 0, out _) < 0)
            return null;
        try
        {
            var id = new Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b");
            if (SHCreateItemFromIDList(pidl, ref id, out var factory) < 0)
                return null;
            try
            {
                const uint iconOnly = 4;
                if (factory.GetImage(new Size { Width = 96, Height = 96 }, iconOnly, out var handle) < 0)
                    return null;
                try
                {
                    var bitmap = Imaging.CreateBitmapSourceFromHBitmap(handle, IntPtr.Zero,
                        Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                    bitmap.Freeze();
                    return bitmap;
                }
                finally { DeleteObject(handle); }
            }
            finally { Marshal.ReleaseComObject(factory); }
        }
        finally { Marshal.FreeCoTaskMem(pidl); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Size { public int Width; public int Height; }

    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig] int GetImage(Size size, uint flags, out IntPtr bitmap);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHParseDisplayName(string name, IntPtr binding, out IntPtr pidl, uint mask, out uint attributes);

    [DllImport("shell32.dll")]
    private static extern int SHCreateItemFromIDList(IntPtr pidl, ref Guid id, out IShellItemImageFactory factory);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr handle);
}
