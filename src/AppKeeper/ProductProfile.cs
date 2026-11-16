using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace AppKeeper;

internal sealed record ProductProfile(string Name, string DataDirectory, bool IsStore)
{
    public static ProductProfile Current { get; } = GetCurrent();

    internal static ProductProfile Resolve(string? family, string executableDirectory, string localAppData) =>
        string.IsNullOrEmpty(family)
            ? new("AppKeeper", executableDirectory, false)
            : new("EasyAppKeeper", Path.Combine(localAppData, "Packages", family, "LocalState", "Data"), true);

    private static ProductProfile GetCurrent()
    {
        const int noPackage = 15700;
        const int insufficientBuffer = 122;
        uint length = 0;
        var result = GetCurrentPackageFamilyName(ref length, null);
        string? family = null;
        if (result == insufficientBuffer)
        {
            var buffer = new StringBuilder((int)length);
            result = GetCurrentPackageFamilyName(ref length, buffer);
            if (result != 0)
                throw new Win32Exception(result);
            family = buffer.ToString();
        }
        else if (result != noPackage)
            throw new Win32Exception(result);

        return Resolve(family, AppContext.BaseDirectory,
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int GetCurrentPackageFamilyName(ref uint length, StringBuilder? family);
}
