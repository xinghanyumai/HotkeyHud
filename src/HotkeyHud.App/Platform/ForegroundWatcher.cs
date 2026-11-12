using System.Diagnostics;
using System.Runtime.InteropServices;

namespace HotkeyHud.App.Platform;

/// <summary>
/// 前台窗口变化监听（<c>EVENT_SYSTEM_FOREGROUND</c>）。
/// 坑 #7：回调委托必须用字段持有，否则被 GC 回收后静默失效。
/// </summary>
public sealed class ForegroundWatcher : IDisposable
{
    private readonly NativeMethods.WinEventDelegate _callback;
    private IntPtr _hook;
    private bool _disposed;

    public ForegroundWatcher()
    {
        _callback = OnWinEvent;
        _hook = NativeMethods.SetWinEventHook(
            NativeMethods.EVENT_SYSTEM_FOREGROUND,
            NativeMethods.EVENT_SYSTEM_FOREGROUND,
            IntPtr.Zero,
            _callback,
            0,
            0,
            NativeMethods.WINEVENT_OUTOFCONTEXT);

        if (_hook == IntPtr.Zero)
        {
            throw new InvalidOperationException(
                $"SetWinEventHook 失败，Win32 错误码 {Marshal.GetLastWin32Error()}");
        }
    }

    public event Action? ForegroundChanged;

    private void OnWinEvent(
        IntPtr hWinEventHook, uint eventType, IntPtr hwnd,
        int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
    {
        // 系统事件回调必须吞异常（红线 3），否则一次异常会让整条监听链静默失效
        try
        {
            ForegroundChanged?.Invoke();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[HotkeyHud] 前台切换回调异常：{ex}");
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_hook != IntPtr.Zero)
        {
            NativeMethods.UnhookWinEvent(_hook);
            _hook = IntPtr.Zero;
        }

        GC.KeepAlive(_callback);
    }
}
