using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Forms = System.Windows.Forms;
using Point = System.Drawing.Point;

namespace AppKeeper;

public partial class FloatingBall : Window
{
    private readonly Action open;
    private readonly Action<BallPosition> save;
    private IntPtr handle;
    private Point? dragOrigin;
    private Point windowOrigin;
    private bool moved;

    internal FloatingBall(Action open, Action<BallPosition> save)
    {
        this.open = open;
        this.save = save;
        InitializeComponent();
    }

    internal void Present(BallPosition? position)
    {
        handle = new WindowInteropHelper(this).EnsureHandle();
        var style = GetWindowLong(handle, -20);
        SetWindowLong(handle, -20, style | 0x08000000); // WS_EX_NOACTIVATE
        Place(position);
        if (!IsVisible)
            Show();
    }

    internal void Place(BallPosition? position)
    {
        if (handle == IntPtr.Zero || dragOrigin.HasValue)
            return;
        var screen = Forms.Screen.AllScreens.FirstOrDefault(screen => screen.DeviceName == position?.Monitor) ??
            Forms.Screen.FromPoint(Forms.Cursor.Position);
        var area = screen.WorkingArea;
        var (width, height) = PhysicalSize(screen);
        var x = position?.X ?? area.Right - area.Left - width;
        var y = position?.Y ?? (area.Height - height) / 2;
        var edge = position?.Edge ?? DockEdge.Right;
        Face.Clip = edge switch
        {
            DockEdge.Left => new RectangleGeometry(new Rect(30, 0, 20, 50)),
            DockEdge.Top => new RectangleGeometry(new Rect(0, 30, 50, 20)),
            DockEdge.Right => new RectangleGeometry(new Rect(0, 0, 20, 50)),
            DockEdge.Bottom => new RectangleGeometry(new Rect(0, 0, 50, 20)),
            _ => null
        };
        x = area.Left + Math.Clamp(x, 0, Math.Max(0, area.Width - width));
        y = area.Top + Math.Clamp(y, 0, Math.Max(0, area.Height - height));
        (x, y) = edge switch
        {
            DockEdge.Left => (area.Left - width * 3 / 5, y),
            DockEdge.Top => (x, area.Top - height * 3 / 5),
            DockEdge.Right => (area.Right - width * 2 / 5, y),
            DockEdge.Bottom => (x, area.Bottom - height * 2 / 5),
            _ => (x, y)
        };
        SetWindowPos(handle, new IntPtr(-1), x, y, width, height, 0x0010);
    }

    private void OpenOnHover(object sender, MouseEventArgs e)
    {
        if (!dragOrigin.HasValue && e.LeftButton == MouseButtonState.Released)
            open();
    }

    private void BeginDrag(object sender, MouseButtonEventArgs e)
    {
        if (!GetWindowRect(handle, out var window))
            return;
        dragOrigin = Forms.Cursor.Position;
        windowOrigin = new(window.Left, window.Top);
        moved = false;
        Face.CaptureMouse();
        e.Handled = true;
    }

    private void ContinueDrag(object sender, MouseEventArgs e)
    {
        if (dragOrigin is not { } origin || e.LeftButton != MouseButtonState.Pressed)
            return;
        var cursor = Forms.Cursor.Position;
        var dx = cursor.X - origin.X;
        var dy = cursor.Y - origin.Y;
        if (!moved && Math.Abs(dx) < 4 && Math.Abs(dy) < 4)
            return;
        moved = true;
        Face.Clip = null;
        var (width, height) = PhysicalSize(Forms.Screen.FromPoint(cursor));
        SetWindowPos(handle, new IntPtr(-1), windowOrigin.X + dx, windowOrigin.Y + dy, width, height, 0x0010);
    }

    private void FinishDrag(object sender, MouseButtonEventArgs e)
    {
        if (!dragOrigin.HasValue)
            return;
        EndDrag();
        if (moved)
            DockAndSave();
        else
            open();
        e.Handled = true;
    }

    private void CancelDrag(object sender, MouseEventArgs e)
    {
        if (!dragOrigin.HasValue)
            return;
        EndDrag();
        if (moved)
            DockAndSave();
    }

    private void EndDrag()
    {
        dragOrigin = null;
        Face.ReleaseMouseCapture();
    }

    private void DockAndSave()
    {
        if (!GetWindowRect(handle, out var rect))
            return;
        var screen = Forms.Screen.FromPoint(Forms.Cursor.Position);
        var area = screen.WorkingArea;
        var (width, height) = PhysicalSize(screen);
        var threshold = width / 2;
        var edges = new (int Distance, DockEdge Edge)[]
        {
            (Math.Abs(rect.Left - area.Left), DockEdge.Left),
            (Math.Abs(rect.Top - area.Top), DockEdge.Top),
            (Math.Abs(rect.Right - area.Right), DockEdge.Right),
            (Math.Abs(rect.Bottom - area.Bottom), DockEdge.Bottom)
        };
        var edge = edges.MinBy(candidate => candidate.Distance);
        var position = new BallPosition(rect.Left - area.Left, rect.Top - area.Top,
            screen.DeviceName, edge.Distance <= threshold ? edge.Edge : DockEdge.None);
        Place(position);
        save(position);
    }

    private static (int Width, int Height) PhysicalSize(Forms.Screen screen)
    {
        var bounds = screen.Bounds;
        var point = new Point(bounds.Left + bounds.Width / 2, bounds.Top + bounds.Height / 2);
        var monitor = MonitorFromPoint(point, 2);
        return GetDpiForMonitor(monitor, 0, out var x, out var y) == 0
            ? ((int)Math.Round(50 * x / 96d), (int)Math.Round(50 * y / 96d))
            : (50, 50);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr window, int index);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr window, int index, int value);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr window, out NativeRect rect);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(Point point, uint flags);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint x, out uint y);
}
