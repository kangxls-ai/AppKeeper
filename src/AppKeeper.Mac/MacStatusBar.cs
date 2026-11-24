using System.Runtime.InteropServices;

namespace AppKeeper;

internal sealed class MacStatusBar : IDisposable
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void Callback(int command);
    private readonly Callback callback;
    internal MacStatusBar(Action<int> action)
    {
        callback=command=>Avalonia.Threading.Dispatcher.UIThread.Post(()=>action(command));
        Create(callback,Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"..","Resources","AppIcon.icns")));
    }
    public void Dispose(){Destroy();GC.KeepAlive(callback);}
    [DllImport("EasyAppKeeper.StatusItem.dylib",EntryPoint="EasyAppKeeperStatusCreate",CallingConvention=CallingConvention.Cdecl)] private static extern void Create(Callback callback,[MarshalAs(UnmanagedType.LPUTF8Str)] string icon);
    [DllImport("EasyAppKeeper.StatusItem.dylib",EntryPoint="EasyAppKeeperStatusDispose",CallingConvention=CallingConvention.Cdecl)] private static extern void Destroy();
    [DllImport("EasyAppKeeper.StatusItem.dylib",EntryPoint="EasyAppKeeperStatusTestClick",CallingConvention=CallingConvention.Cdecl)] internal static extern int TestClick();
    [DllImport("EasyAppKeeper.StatusItem.dylib",EntryPoint="EasyAppKeeperTestDockReopen",CallingConvention=CallingConvention.Cdecl)] internal static extern int TestDockReopen();
}
