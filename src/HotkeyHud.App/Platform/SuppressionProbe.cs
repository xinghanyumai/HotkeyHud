using System.Text;
using HotkeyHud.Core;

namespace HotkeyHud.App.Platform;

/// <summary>
/// §2.1 的六个抑制信号采集。
/// <para>
/// 缓存策略：只有进程名 / 可执行路径按 HWND 缓存（它们要 <c>OpenProcess</c>，是最贵的一步）；
/// 窗口矩形、样式、类名、系统通知状态每次都重采——全屏状态可能在同一个窗口里变化（F11 切进切出），
/// 缓存这些会让 2 秒轮询形同虚设。
/// </para>
/// <para>提权窗口 <c>OpenProcess</c> 返回 0 是常态：返回空串，不抛异常、不写日志。</para>
/// </summary>
public sealed class SuppressionProbe
{
    private const int FullscreenRectTolerance = 2;

    // SHQueryUserNotificationState 的取值（§2.1 只认 2/3/4）
    private const int QunsBusy = 2;
    private const int QunsRunningD3DFullScreen = 3;
    private const int QunsPresentationMode = 4;

    private IntPtr _cachedWindow;
    private string _cachedProcessName = string.Empty;
    private string _cachedExecutablePath = string.Empty;

    /// <summary>最近一次读到的系统通知状态原值，仅用于诊断输出。</summary>
    public int LastNotificationState { get; private set; }

    public GameSignal Capture()
    {
        IntPtr window = NativeMethods.GetForegroundWindow();
        if (window == IntPtr.Zero)
        {
            return default;
        }

        if (window != _cachedWindow)
        {
            RefreshProcess(window);
            _cachedWindow = window;
        }

        return new GameSignal(
            ExclusiveFullscreen: QueryExclusiveFullscreen(),
            CoversWholeMonitor: CoversWholeMonitor(window),
            HasCaption: HasCaption(window),
            ProcessName: _cachedProcessName,
            WindowClass: ReadWindowClass(window),
            ExecutablePath: _cachedExecutablePath);
    }

    /// <summary>前台换了就调用，强制下次重新 <c>OpenProcess</c>。</summary>
    public void Invalidate() => _cachedWindow = IntPtr.Zero;

    private bool QueryExclusiveFullscreen()
    {
        if (NativeMethods.SHQueryUserNotificationState(out int state) != 0)
        {
            LastNotificationState = 0;
            return false;
        }

        LastNotificationState = state;
        return state is QunsBusy or QunsRunningD3DFullScreen or QunsPresentationMode;
    }

    private static bool CoversWholeMonitor(IntPtr window)
    {
        if (!NativeMethods.GetWindowRect(window, out NativeMethods.RECT rect))
        {
            return false;
        }

        IntPtr monitor = NativeMethods.MonitorFromWindow(window, NativeMethods.MONITOR_DEFAULTTONEAREST);
        if (monitor == IntPtr.Zero)
        {
            return false;
        }

        var info = new NativeMethods.MONITORINFO
        {
            cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MONITORINFO>(),
        };

        if (!NativeMethods.GetMonitorInfo(monitor, ref info))
        {
            return false;
        }

        NativeMethods.RECT screen = info.rcMonitor;
        return Math.Abs(rect.Left - screen.Left) <= FullscreenRectTolerance
               && Math.Abs(rect.Top - screen.Top) <= FullscreenRectTolerance
               && Math.Abs(rect.Right - screen.Right) <= FullscreenRectTolerance
               && Math.Abs(rect.Bottom - screen.Bottom) <= FullscreenRectTolerance;
    }

    private static bool HasCaption(IntPtr window)
    {
        long style = NativeMethods.GetWindowLongPtr(window, NativeMethods.GWL_STYLE).ToInt64();
        return (style & NativeMethods.WS_CAPTION) == NativeMethods.WS_CAPTION;
    }

    private static string ReadWindowClass(IntPtr window)
    {
        var buffer = new StringBuilder(256);
        int length = NativeMethods.GetClassName(window, buffer, buffer.Capacity);
        return length > 0 ? buffer.ToString(0, length) : string.Empty;
    }

    private void RefreshProcess(IntPtr window)
    {
        _cachedProcessName = string.Empty;
        _cachedExecutablePath = string.Empty;

        NativeMethods.GetWindowThreadProcessId(window, out uint processId);
        if (processId == 0)
        {
            return;
        }

        IntPtr process = NativeMethods.OpenProcess(
            NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION, false, processId);
        if (process == IntPtr.Zero)
        {
            // 坑 #10：前台是提权窗口，这是常态，不是错误
            return;
        }

        try
        {
            var buffer = new StringBuilder(1024);
            uint size = (uint)buffer.Capacity;
            if (NativeMethods.QueryFullProcessImageNameW(process, 0, buffer, ref size))
            {
                _cachedExecutablePath = buffer.ToString();
                _cachedProcessName = Path.GetFileName(_cachedExecutablePath);
            }
        }
        finally
        {
            NativeMethods.CloseHandle(process);
        }
    }
}
