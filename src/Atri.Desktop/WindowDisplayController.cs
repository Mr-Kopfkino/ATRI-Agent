using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;

namespace Atri.Desktop;

/// <summary>Two borderless, non-resizable modes. Window size is physical pixels,
/// not DIPs. Does not alter display resolution, wallpaper, or process DPI.</summary>
public sealed class WindowDisplayController : IDisposable
{
    public const int WindowPixelWidth = 1280, WindowPixelHeight = 720;
    private readonly Window window;
    private readonly DesktopHost desktop;
    private HwndSource? source;
    private bool applying, pending, disposed;
    public string Mode { get; private set; }
    public string State { get; private set; } = "显示模式尚未初始化";
    public event Action<string>? ModeChanged;

    public WindowDisplayController(Window window, DesktopHost desktopHost, string initialMode = "window")
    {
        ArgumentNullException.ThrowIfNull(window); ArgumentNullException.ThrowIfNull(desktopHost);
        ValidateMode(initialMode); window.Dispatcher.VerifyAccess();
        this.window = window; desktop = desktopHost; Mode = initialMode;
        window.SourceInitialized += Initialized; window.Loaded += Loaded;
        window.PreviewKeyDown += KeyDown; window.Closed += Closed;
        if (new WindowInteropHelper(window).Handle != IntPtr.Zero) Initialize();
    }

    public DesktopMonitor CurrentMonitor()
    {
        window.Dispatcher.VerifyAccess();
        IntPtr hwnd = new WindowInteropHelper(window).EnsureHandle();
        IntPtr handle = Native.MonitorFromWindow(hwnd, 2);
        var info = new Native.MonitorInfo { Size = (uint)Marshal.SizeOf<Native.MonitorInfo>() };
        Check(Native.GetMonitorInfo(handle, ref info), "读取当前显示器");
        uint dpi = Native.GetDpiForWindow(hwnd);
        return new DesktopMonitor(info.Device ?? "", Convert(info.Bounds), Convert(info.Work), (info.Flags & 1) != 0, dpi, dpi);
    }

    public bool Toggle() => Apply(Mode == "fullscreen" ? "window" : "fullscreen");

    public bool Apply(string mode)
    {
        ValidateMode(mode); window.Dispatcher.VerifyAccess();
        if (disposed) { State = "显示控制器已关闭"; return false; }
        if (applying) return true;
        applying = true;
        string previousMode = Mode;
        try
        {
            if (desktop.IsAttached && !desktop.Detach(window)) { State = desktop.State; return false; }
            IntPtr hwnd = new WindowInteropHelper(window).EnsureHandle();
            Native.GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid != Environment.ProcessId) throw new InvalidOperationException("只允许改变本进程窗口。");
            window.WindowState = WindowState.Normal;
            window.WindowStyle = WindowStyle.None; window.ResizeMode = ResizeMode.NoResize;
            window.SizeToContent = SizeToContent.Manual;
            var screen = CurrentMonitor();
            DesktopRectangle bounds = Target(mode, screen);
            ApplyBounds(hwnd, bounds);
            Mode = mode;
            State = mode == "fullscreen" ? $"全屏 · {screen.Device} · F11/ESC 恢复窗口" : "窗口 · 1280×720 物理像素 · F11 全屏";
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or IOException or ArgumentException)
        {
            // Never leave a partially configured fullscreen window inaccessible.
            // A failure attempts the exact ordinary-window mode on the same screen.
            try
            {
                if (!desktop.IsAttached)
                {
                    var hwnd = new WindowInteropHelper(window).Handle;
                    window.WindowState = WindowState.Normal; window.WindowStyle = WindowStyle.None; window.ResizeMode = ResizeMode.NoResize;
                    window.SizeToContent = SizeToContent.Manual;
                    ApplyBounds(hwnd, Target("window", CurrentMonitor()));
                    Mode = "window";
                }
            }
            catch (Exception recovery) when (recovery is Win32Exception or InvalidOperationException or IOException or ArgumentException)
            { State = "显示切换失败，窗口恢复未完成：" + exception.Message + "；" + recovery.Message; return false; }
            State = "显示切换失败，已保留普通窗口：" + exception.Message;
            if (previousMode != Mode) ModeChanged?.Invoke(Mode);
            return false;
        }
        finally { applying = false; }
        if (previousMode != Mode) ModeChanged?.Invoke(Mode);
        return true;
    }

    private static DesktopRectangle Target(string mode, DesktopMonitor screen)
    {
        if (mode == "fullscreen") return screen.Bounds;
        int left = screen.WorkArea.Left + (screen.WorkArea.Width - WindowPixelWidth) / 2;
        int top = screen.WorkArea.Top + (screen.WorkArea.Height - WindowPixelHeight) / 2;
        return new(left, top, left + WindowPixelWidth, top + WindowPixelHeight);
    }

    private void ApplyBounds(IntPtr hwnd, DesktopRectangle bounds)
    {
        uint dpi = Native.GetDpiForWindow(hwnd);
        if (dpi == 0) throw new IOException("窗口 DPI 不可用。");
        window.MinWidth = 0; window.MinHeight = 0; window.MaxWidth = double.PositiveInfinity; window.MaxHeight = double.PositiveInfinity;
        window.Width = bounds.Width * 96d / dpi; window.Height = bounds.Height * 96d / dpi;
        // Left/Top WPF properties use DIPs whose origin differs between screens.
        // Native physical coordinates avoid mixed-DPI and negative-origin drift.
        Check(Native.SetWindowPos(hwnd, IntPtr.Zero, bounds.Left, bounds.Top, bounds.Width, bounds.Height,
            0x0004 | 0x0010 | 0x0020 | 0x0040), "设置物理窗口尺寸");
        Check(Native.GetClientRect(hwnd, out Native.Rect client), "读取窗口客户区");
        if (client.Right - client.Left != bounds.Width || client.Bottom - client.Top != bounds.Height)
            throw new IOException($"客户区尺寸校验失败：{client.Right - client.Left}×{client.Bottom - client.Top}。");
        // NoResize removes interactive borders; constraints also prevent layout
        // requests from changing the selected mode until our next mode/DPI change.
        dpi = Native.GetDpiForWindow(hwnd);
        window.MinWidth = window.MaxWidth = bounds.Width * 96d / dpi;
        window.MinHeight = window.MaxHeight = bounds.Height * 96d / dpi;
    }

    private void Initialized(object? sender, EventArgs args) => Initialize();
    private void Initialize()
    {
        if (disposed) return;
        source = HwndSource.FromHwnd(new WindowInteropHelper(window).Handle);
        source?.RemoveHook(Message); source?.AddHook(Message);
        Apply(Mode);
    }
    private void Loaded(object sender, RoutedEventArgs args)
    { window.Loaded -= Loaded; if (!desktop.IsAttached) Apply(Mode); }

    private IntPtr Message(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        // WPF first processes WM_DPICHANGED's recommended rectangle. Reapply our
        // physical-pixel contract afterwards, without overriding WPF's DPI state.
        if (message is 0x02E0 or 0x007E) Schedule();
        return IntPtr.Zero;
    }
    private void Schedule()
    {
        if (pending || disposed) return; pending = true;
        window.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        { pending = false; if (!disposed && !desktop.IsAttached) Apply(Mode); });
    }
    private void KeyDown(object sender, KeyEventArgs args)
    {
        if (disposed || desktop.IsAttached) return;
        if (args.Key == Key.F11) { args.Handled = true; Toggle(); }
        else if (args.Key == Key.Escape && Mode == "fullscreen") { args.Handled = true; Apply("window"); }
    }
    private void Closed(object? sender, EventArgs args) => Dispose();
    public void Dispose()
    {
        if (disposed) return; window.Dispatcher.VerifyAccess(); disposed = true;
        source?.RemoveHook(Message); source = null;
        window.SourceInitialized -= Initialized; window.Loaded -= Loaded;
        window.PreviewKeyDown -= KeyDown; window.Closed -= Closed;
    }
    private static void ValidateMode(string mode)
    { if (mode is not ("window" or "fullscreen")) throw new ArgumentException("显示模式须为 window 或 fullscreen。", nameof(mode)); }
    private static DesktopRectangle Convert(Native.Rect r) => new(r.Left, r.Top, r.Right, r.Bottom);
    private static void Check(bool result, string action)
    { if (!result) throw new Win32Exception(Marshal.GetLastPInvokeError(), action + "失败。"); }
    private static class Native
    {
        [StructLayout(LayoutKind.Sequential)] internal struct Rect { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] internal struct MonitorInfo
        { public uint Size; public Rect Bounds, Work; public uint Flags; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string? Device; }
        [DllImport("user32.dll")] internal static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
        [DllImport("user32.dll")] internal static extern uint GetDpiForWindow(IntPtr hwnd);
        [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
        [DllImport("user32.dll", SetLastError = true)] internal static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);
        [DllImport("user32.dll", SetLastError = true)] internal static extern bool GetClientRect(IntPtr hwnd, out Rect bounds);
    }
}
