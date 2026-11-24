using Microsoft.Win32;
using System;
using System.IO;
using System.Runtime.InteropServices;

namespace AppKeeper;

internal static class ShellMenu
{
    internal const string Title="添加到 EasyAppKeeper";
    internal static void Apply(ProductProfile profile,bool enabled)
    {
        Directory.CreateDirectory(profile.DataDirectory);
        var marker=Path.Combine(profile.DataDirectory,"shell-disabled");
        if(enabled){if(File.Exists(marker))File.Delete(marker);}else File.WriteAllText(marker,"");
        if(profile.IsStore)return;
        var executable=Environment.ProcessPath!;
        if(Path.GetFileName(executable).Equals("dotnet.exe",StringComparison.OrdinalIgnoreCase))return;
        foreach(var kind in new[]{"AllFilesystemObjects","*","Directory"})
        {
            var path=@"Software\Classes\"+kind+@"\shell\EasyAppKeeper.Add";
            if(enabled&&kind=="AllFilesystemObjects")
            {
                using var key=Registry.CurrentUser.CreateSubKey(path);
                key.SetValue("",Title);key.SetValue("Icon",executable+",0");key.SetValue("MultiSelectModel","Player");key.SetValue("OwnerPath",executable);
                using var command=key.CreateSubKey("command");command.SetValue("",Quote(executable)+" --add \"%1\"");
            }
            else
            {
                using var key=Registry.CurrentUser.OpenSubKey(path);
                if(string.Equals(key?.GetValue("OwnerPath") as string,executable,StringComparison.OrdinalIgnoreCase))Registry.CurrentUser.DeleteSubKeyTree(path,false);
            }
        }
        SHChangeNotify(0x08000000,0,IntPtr.Zero,IntPtr.Zero);
    }
    internal static string Quote(string value)=>"\""+value+"\"";
    [DllImport("shell32.dll")] private static extern void SHChangeNotify(uint eventId,uint flags,IntPtr item1,IntPtr item2);
}
