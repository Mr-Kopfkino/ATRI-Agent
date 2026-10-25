using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;

namespace Atri.Desktop;

public sealed record DesktopRectangle(int Left, int Top, int Right, int Bottom)
{
    public int Width => Right - Left;
    public int Height => Bottom - Top;
}
public sealed record DesktopMonitor(string Device, DesktopRectangle Bounds, DesktopRectangle WorkArea, bool Primary, uint DpiX, uint DpiY);
public sealed record DesktopHostSnapshot(long WindowHandle, long ParentHandle, string ParentClass, long Style, long ExtendedStyle,
    uint Dpi, int Awareness, int MonitorCount, bool Attached, bool ShowInTaskbar, long OwnerHandle = 0);

/// <summary>Opt-in embedding of this process's own WPF window below desktop icons.
/// No wallpaper settings, registry, external window styles, or helper processes
/// are changed. WorkerW is an Explorer implementation detail, so fail closed.</summary>
public sealed class DesktopHost : IDisposable
{
    private const int StyleIndex = -16, ExStyleIndex = -20, OwnerIndex = -8;
    private const long Child = 0x40000000, Popup = 0x80000000, Caption = 0x00C00000,
        ThickFrame = 0x00040000, SystemMenu = 0x00080000, MinMaxBoxes = 0x00030000,
        AppWindow = 0x00040000, ToolWindow = 0x00000080, Topmost = 0x00000008;
    private const int EscapeHotkey = 0x4154;
    private SavedWindow? saved;
    private IntPtr host;
    private HwndSource? source;
    private DispatcherTimer? monitor;
    private bool hotkey, disposed;
    public string State { get; private set; } = "普通窗口（桌面模式未启用）";
    public bool IsAttached => saved is not null && host != IntPtr.Zero;

    public IReadOnlyList<DesktopMonitor> Monitors()
    {
        var result = new List<DesktopMonitor>();
        Native.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr handle, IntPtr dc, ref Native.Rect rectangle, IntPtr data) =>
        {
            var info = new Native.MonitorInfo { Size = (uint)Marshal.SizeOf<Native.MonitorInfo>() };
            if (!Native.GetMonitorInfo(handle, ref info)) return true;
            uint x = 96, y = 96;
            try { if (Native.GetDpiForMonitor(handle, 0, out uint actualX, out uint actualY) == 0) { x = actualX; y = actualY; } }
            catch (EntryPointNotFoundException) { }
            result.Add(new DesktopMonitor(info.Device ?? "", Convert(info.Bounds), Convert(info.Work), (info.Flags & 1) != 0, x, y));
            return true;
        }, IntPtr.Zero);
        return result;
    }

    public DesktopHostSnapshot Inspect(Window window)
    {
        window.Dispatcher.VerifyAccess();
        IntPtr hwnd = new WindowInteropHelper(window).EnsureHandle();
        IntPtr parent = Native.GetParent(hwnd);
        return new(hwnd.ToInt64(), parent.ToInt64(), Class(parent), GetLong(hwnd, StyleIndex), GetLong(hwnd, ExStyleIndex),
            Native.GetDpiForWindow(hwnd), Native.GetAwarenessFromDpiAwarenessContext(Native.GetWindowDpiAwarenessContext(hwnd)), Monitors().Count,
            saved?.Window == window && IsAttached, window.ShowInTaskbar, Native.GetWindow(hwnd, 4).ToInt64());
    }

    public bool Attach(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        window.Dispatcher.VerifyAccess();
        if (disposed) { State = "桌面宿主已关闭。"; return false; }
        if (IsAttached) return ReferenceEquals(saved!.Window, window);
        try
        {
            IntPtr hwnd = new WindowInteropHelper(window).EnsureHandle();
            Native.GetWindowThreadProcessId(hwnd, out uint ownPid);
            if (ownPid != Environment.ProcessId) throw new InvalidOperationException("只允许挂载本进程自己的窗口。");
            IntPtr target = DiscoverHost(create: true);
            if (target == IntPtr.Zero) throw new InvalidOperationException("没有找到位于桌面图标下方的安全 WorkerW，保持普通窗口。");
            // SetParent across incompatible DPI contexts can reset the child
            // process's awareness. Never change process/global DPI to force it.
            if (!Native.AreDpiAwarenessContextsEqual(Native.GetWindowDpiAwarenessContext(hwnd), Native.GetWindowDpiAwarenessContext(target)))
                throw new InvalidOperationException("Explorer 与应用的 DPI 上下文不同，已拒绝跨模式挂载并保留普通窗口。");
            var placement = new Native.Placement { Length = (uint)Marshal.SizeOf<Native.Placement>() };
            Check(Native.GetWindowPlacement(hwnd, ref placement), "读取窗口位置");
            IntPtr currentMonitor = Native.MonitorFromWindow(hwnd, 2);
            var current = new Native.MonitorInfo { Size = (uint)Marshal.SizeOf<Native.MonitorInfo>() };
            Check(Native.GetMonitorInfo(currentMonitor, ref current), "读取当前显示器");
            saved = new SavedWindow(window, hwnd, (GetLong(hwnd, StyleIndex) & Child) != 0 ? Native.GetParent(hwnd) : IntPtr.Zero,
                Native.GetWindow(hwnd, 4), GetLong(hwnd, StyleIndex), GetLong(hwnd, ExStyleIndex), placement, window.Left, window.Top,
                window.Width, window.Height, window.MinWidth, window.MinHeight, window.MaxWidth, window.MaxHeight, current.Device ?? "");
            host = target;
            source = HwndSource.FromHwnd(hwnd) ?? throw new InvalidOperationException("窗口消息源不可用。");
            source.AddHook(WindowMessage);
            // Temporary hotkey is active only during this explicitly chosen mode;
            // ESC can restore even when the desktop child lacks keyboard focus.
            hotkey = Native.RegisterHotKey(hwnd, EscapeHotkey, 0x4000, 0x1B);
            if (!hotkey) throw new Win32Exception(Marshal.GetLastWin32Error(), "无法注册 ESC 安全恢复键，保持普通窗口。");
            window.PreviewKeyDown += Escape;
            window.Closed += Closed;
            monitor = new DispatcherTimer(TimeSpan.FromSeconds(2), DispatcherPriority.Background, (_, _) => CheckHost(), window.Dispatcher);
            monitor.Start();
            window.MinWidth = 0; window.MinHeight = 0; window.MaxWidth = double.PositiveInfinity; window.MaxHeight = double.PositiveInfinity;
            Native.ShowWindow(hwnd, 9);
            // WS_EX_TOPMOST is a Z-order state: changing its style bit alone
            // does not remove a previously topmost window from that group.
            Check(Native.SetWindowPos(hwnd, new IntPtr(-2), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010), "取消临时顶层排序");
            long style = (saved.Style | Child) & ~(Popup | Caption | ThickFrame | SystemMenu | MinMaxBoxes);
            SetLong(hwnd, StyleIndex, style);
            SetLong(hwnd, ExStyleIndex, (saved.ExtendedStyle | ToolWindow) & ~(AppWindow | Topmost));
            SetParentChecked(hwnd, target);
            if (Native.GetParent(hwnd) != target) throw new IOException("Windows 未接受桌面父窗口。");
            Arrange();
            State = $"桌面模式 · {saved.Device} · ESC 恢复普通窗口";
            return true;
        }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException or IOException or ArgumentException)
        {
            string reason = error.Message;
            if (saved is not null && !Restore(out string? restoreError))
            { State = "桌面挂载失败且恢复未完成：" + reason + "；" + restoreError + "。请按 ESC 或点击恢复窗口重试。"; return false; }
            State = "桌面模式未启用：" + reason;
            return false;
        }
    }

    public bool Detach(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        window.Dispatcher.VerifyAccess();
        if (saved is null) { State = "普通窗口"; return true; }
        if (!ReferenceEquals(saved.Window, window)) { State = "恢复目标不是已挂载窗口。"; return false; }
        bool restored = Restore(out string? error);
        State = restored ? "普通窗口（已恢复位置、样式与任务栏）" : "窗口恢复失败：" + error;
        return restored;
    }

    private bool Restore(out string? error)
    {
        error = null;
        SavedWindow? previous = saved;
        if (previous is null) return true;
        try
        {
            if (!Native.IsWindow(previous.Handle)) throw new InvalidOperationException("原窗口已关闭。");
            IntPtr parent = previous.Parent != IntPtr.Zero && Native.IsWindow(previous.Parent) ? previous.Parent : IntPtr.Zero;
            SetParentChecked(previous.Handle, parent);
            SetLong(previous.Handle, StyleIndex, previous.Style);
            SetLong(previous.Handle, ExStyleIndex, previous.ExtendedStyle);
            IntPtr owner = Native.IsWindow(previous.Owner) ? previous.Owner : IntPtr.Zero;
            if (parent == IntPtr.Zero && Native.GetWindow(previous.Handle, 4) != owner) SetLong(previous.Handle, OwnerIndex, owner.ToInt64());
            previous.Window.MinWidth = previous.MinWidth; previous.Window.MinHeight = previous.MinHeight;
            previous.Window.MaxWidth = previous.MaxWidth; previous.Window.MaxHeight = previous.MaxHeight;
            if (double.IsFinite(previous.Left)) previous.Window.Left = previous.Left;
            if (double.IsFinite(previous.Top)) previous.Window.Top = previous.Top;
            if (double.IsFinite(previous.Width)) previous.Window.Width = previous.Width;
            if (double.IsFinite(previous.Height)) previous.Window.Height = previous.Height;
            var placement = previous.Placement;
            Check(Native.SetWindowPlacement(previous.Handle, ref placement), "恢复窗口位置");
            Check(Native.SetWindowPos(previous.Handle, (previous.ExtendedStyle & Topmost) != 0 ? new IntPtr(-1) : new IntPtr(-2), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0020 | 0x0040), "恢复窗口框架与任务栏");
            bool correctParent = (previous.Style & Child) != 0 ? Native.GetParent(previous.Handle) == parent : Native.GetWindow(previous.Handle, 4) == owner;
            if (!correctParent) throw new IOException("恢复父窗口或 owner 校验失败。");
            previous.Window.Activate();
            StopMonitoring(previous);
            saved = null; host = IntPtr.Zero;
            return true;
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or IOException or ArgumentException)
        {
            error = exception.Message;
            // Keep the original snapshot so a subsequent Restore/Exit can retry.
            return false;
        }
    }

    private void Arrange()
    {
        if (saved is null || host == IntPtr.Zero) return;
        DesktopMonitor? screen = Monitors().FirstOrDefault(item => item.Device == saved.Device) ?? Monitors().FirstOrDefault(item => item.Primary);
        if (screen is null) throw new IOException("没有可用显示器。");
        var point = new Native.Point { X = screen.Bounds.Left, Y = screen.Bounds.Top };
        Check(Native.ScreenToClient(host, ref point), "换算桌面物理坐标");
        Check(Native.GetClientRect(host, out Native.Rect available), "读取桌面宿主区域");
        if (point.X < available.Left || point.Y < available.Top || point.X + screen.Bounds.Width > available.Right || point.Y + screen.Bounds.Height > available.Bottom)
            throw new IOException("当前 WorkerW 不覆盖所选显示器，已回退普通窗口。");
        Check(Native.SetWindowPos(saved.Handle, new IntPtr(1), point.X, point.Y, screen.Bounds.Width, screen.Bounds.Height, 0x0010 | 0x0020 | 0x0040), "设置桌面窗口大小");
    }

    private void CheckHost()
    {
        if (saved is null) return;
        if (!Native.IsWindow(host) || !Native.IsWindow(saved.Handle) || Native.GetParent(saved.Handle) != host)
        { var window = saved.Window; Detach(window); State += " · 桌面宿主已变化"; }
    }

    private IntPtr WindowMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (saved is null) return IntPtr.Zero;
        if (message == 0x0312 && wParam.ToInt32() == EscapeHotkey)
        { handled = true; Detach(saved.Window); }
        else if (message is 0x02E0 or 0x007E)
            saved.Window.Dispatcher.BeginInvoke(() => { if (IsAttached) try { Arrange(); } catch { if (saved is not null) Detach(saved.Window); } });
        return IntPtr.Zero;
    }

    private void Escape(object sender, KeyEventArgs args)
    { if (args.Key == Key.Escape && saved is not null) { args.Handled = true; Detach(saved.Window); } }
    private void Closed(object? sender, EventArgs args)
    { if (saved is not null) StopMonitoring(saved); saved = null; host = IntPtr.Zero; State = "窗口已关闭"; }
    private void StopMonitoring(SavedWindow previous)
    {
        monitor?.Stop(); monitor = null;
        if (hotkey) Native.UnregisterHotKey(previous.Handle, EscapeHotkey);
        hotkey = false;
        source?.RemoveHook(WindowMessage); source = null;
        previous.Window.PreviewKeyDown -= Escape; previous.Window.Closed -= Closed;
    }

    private static IntPtr DiscoverHost(bool create)
    {
        IntPtr progman = Native.FindWindow("Progman", null), shell = Native.GetShellWindow();
        if (progman == IntPtr.Zero || shell == IntPtr.Zero) return IntPtr.Zero;
        Native.GetWindowThreadProcessId(progman, out uint explorer);
        Native.GetWindowThreadProcessId(shell, out uint shellPid);
        if (explorer == 0 || explorer != shellPid) return IntPtr.Zero;
        if (create) Native.SendMessageTimeout(progman, 0x052C, IntPtr.Zero, IntPtr.Zero, 0x0002 | 0x0020, 750, out _);
        IntPtr found = IntPtr.Zero;
        Native.EnumWindows((window, _) =>
        {
            IntPtr view = Native.FindWindowEx(window, IntPtr.Zero, "SHELLDLL_DefView", null);
            if (view == IntPtr.Zero) return true;
            // Current Windows 11 can put the wallpaper WorkerW directly under
            // Progman after its icon view. Older Explorer builds use a separate
            // top-level WorkerW after the icon-host window. In both layouts we
            // require the wallpaper sibling to be below the actual icon view.
            IntPtr childWorker = Native.FindWindowEx(window, view, "WorkerW", null);
            if (UsableHost(childWorker, explorer)) { found = childWorker; return false; }
            IntPtr worker = Native.FindWindowEx(IntPtr.Zero, window, "WorkerW", null);
            if (!UsableHost(worker, explorer)) return true;
            found = worker; return false;
        }, IntPtr.Zero);
        return found;
    }

    private static bool UsableHost(IntPtr worker, uint explorer)
    {
        if (worker == IntPtr.Zero || !Native.IsWindowVisible(worker) || Native.FindWindowEx(worker, IntPtr.Zero, "SHELLDLL_DefView", null) != IntPtr.Zero) return false;
        Native.GetWindowThreadProcessId(worker, out uint pid);
        return pid == explorer && Native.GetClientRect(worker, out Native.Rect bounds) && bounds.Right - bounds.Left > 1 && bounds.Bottom - bounds.Top > 1;
    }

    private static string Class(IntPtr window)
    { if (window == IntPtr.Zero) return ""; var result = new StringBuilder(128); Native.GetClassName(window, result, result.Capacity); return result.ToString(); }
    private static DesktopRectangle Convert(Native.Rect rectangle) => new(rectangle.Left, rectangle.Top, rectangle.Right, rectangle.Bottom);
    private static long GetLong(IntPtr window, int index) => IntPtr.Size == 8 ? Native.GetWindowLongPtr(window, index).ToInt64() : Native.GetWindowLong(window, index);
    private static void SetLong(IntPtr window, int index, long value)
    {
        Marshal.SetLastPInvokeError(0);
        long previous = IntPtr.Size == 8 ? Native.SetWindowLongPtr(window, index, new IntPtr(value)).ToInt64() : Native.SetWindowLong(window, index, (int)value);
        if (previous == 0 && Marshal.GetLastPInvokeError() != 0) throw new Win32Exception(Marshal.GetLastPInvokeError(), $"设置窗口属性 {index} 失败。");
    }
    private static void SetParentChecked(IntPtr window, IntPtr parent)
    { Marshal.SetLastPInvokeError(0); IntPtr previous = Native.SetParent(window, parent); if (previous == IntPtr.Zero && Marshal.GetLastPInvokeError() != 0) throw new Win32Exception(Marshal.GetLastPInvokeError(), "设置窗口父级失败。"); }
    private static void Check(bool result, string operation) { if (!result) throw new Win32Exception(Marshal.GetLastPInvokeError(), operation + "失败。"); }
    public void Dispose()
    { if (disposed) return; if (saved is not null) Detach(saved.Window); disposed = true; }

    private sealed record SavedWindow(Window Window, IntPtr Handle, IntPtr Parent, IntPtr Owner, long Style, long ExtendedStyle,
        Native.Placement Placement, double Left, double Top, double Width, double Height, double MinWidth, double MinHeight,
        double MaxWidth, double MaxHeight, string Device);

    private static class Native
    {
        internal delegate bool EnumWindowsProc(IntPtr window, IntPtr data);
        internal delegate bool EnumMonitorsProc(IntPtr monitor, IntPtr dc, ref Rect bounds, IntPtr data);
        [StructLayout(LayoutKind.Sequential)] internal struct Point { public int X, Y; }
        [StructLayout(LayoutKind.Sequential)] internal struct Rect { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)] internal struct Placement { public uint Length, Flags, Show; public Point Min, Max; public Rect Normal; }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] internal struct MonitorInfo { public uint Size; public Rect Bounds, Work; public uint Flags; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string? Device; }
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern IntPtr FindWindow(string className, string? title);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string className, string? title);
        [DllImport("user32.dll")] internal static extern IntPtr GetShellWindow();
        [DllImport("user32.dll")] internal static extern bool EnumWindows(EnumWindowsProc callback, IntPtr data);
        [DllImport("user32.dll")] internal static extern bool EnumDisplayMonitors(IntPtr dc, IntPtr clip, EnumMonitorsProc callback, IntPtr data);
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
        [DllImport("user32.dll")] internal static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);
        [DllImport("shcore.dll")] internal static extern int GetDpiForMonitor(IntPtr monitor, int kind, out uint x, out uint y);
        [DllImport("user32.dll")] internal static extern uint GetDpiForWindow(IntPtr window);
        [DllImport("user32.dll")] internal static extern IntPtr GetWindowDpiAwarenessContext(IntPtr window);
        [DllImport("user32.dll")] internal static extern int GetAwarenessFromDpiAwarenessContext(IntPtr context);
        [DllImport("user32.dll")] internal static extern bool AreDpiAwarenessContextsEqual(IntPtr first, IntPtr second);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern int GetClassName(IntPtr window, StringBuilder text, int maximum);
        [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
        [DllImport("user32.dll")] internal static extern IntPtr GetParent(IntPtr window);
        [DllImport("user32.dll")] internal static extern IntPtr GetWindow(IntPtr window, uint command);
        [DllImport("user32.dll", SetLastError = true)] internal static extern IntPtr SetParent(IntPtr window, IntPtr parent);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)] internal static extern IntPtr GetWindowLongPtr(IntPtr window, int index);
        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)] internal static extern IntPtr SetWindowLongPtr(IntPtr window, int index, IntPtr value);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)] internal static extern int GetWindowLong(IntPtr window, int index);
        [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)] internal static extern int SetWindowLong(IntPtr window, int index, int value);
        [DllImport("user32.dll", SetLastError = true)] internal static extern bool GetWindowPlacement(IntPtr window, ref Placement placement);
        [DllImport("user32.dll", SetLastError = true)] internal static extern bool SetWindowPlacement(IntPtr window, ref Placement placement);
        [DllImport("user32.dll", SetLastError = true)] internal static extern bool GetClientRect(IntPtr window, out Rect rectangle);
        [DllImport("user32.dll", SetLastError = true)] internal static extern bool ScreenToClient(IntPtr window, ref Point point);
        [DllImport("user32.dll", SetLastError = true)] internal static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
        [DllImport("user32.dll")] internal static extern bool ShowWindow(IntPtr window, int command);
        [DllImport("user32.dll")] internal static extern bool IsWindow(IntPtr window);
        [DllImport("user32.dll")] internal static extern bool IsWindowVisible(IntPtr window);
        [DllImport("user32.dll", SetLastError = true)] internal static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint key);
        [DllImport("user32.dll")] internal static extern bool UnregisterHotKey(IntPtr window, int id);
        [DllImport("user32.dll", SetLastError = true)] internal static extern IntPtr SendMessageTimeout(IntPtr window, uint message, IntPtr wParam, IntPtr lParam, uint flags, uint timeout, out IntPtr result);
    }
}
