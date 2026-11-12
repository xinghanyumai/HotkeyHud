using System.Runtime.InteropServices;
using System.Text;

namespace HotkeyHud.App.Platform;

/// <summary>
/// 前台窗口的进程名查询。结果缓存，由 <see cref="Invalidate"/> 失效——
/// <c>OpenProcess</c> 有实打实的开销，不能在每次按键时都查（§2.1 采集时机）。
/// </summary>
public sealed class ForegroundProbe
{
    private string _processName = string.Empty;
    private bool _valid;

    /// <summary>前台窗口的进程文件名（含 <c>.exe</c>）。取不到时返回空串，不抛异常。</summary>
    public string ProcessName
    {
        get
        {
            if (!_valid)
            {
                _processName = ReadForegroundProcessName();
                _valid = true;
            }

            return _processName;
        }
    }

    public void Invalidate() => _valid = false;

    private static string ReadForegroundProcessName()
    {
        IntPtr window = NativeMethods.GetForegroundWindow();
        if (window == IntPtr.Zero)
        {
            return string.Empty;
        }

        NativeMethods.GetWindowThreadProcessId(window, out uint processId);
        if (processId == 0)
        {
            return string.Empty;
        }

        IntPtr process = NativeMethods.OpenProcess(
            NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION, false, processId);
        if (process == IntPtr.Zero)
        {
            // 坑 #10：前台是提权窗口时 OpenProcess 返回 0，容错为空串，不抛异常、不崩
            return string.Empty;
        }

        try
        {
            var buffer = new StringBuilder(1024);
            uint size = (uint)buffer.Capacity;
            if (!NativeMethods.QueryFullProcessImageNameW(process, 0, buffer, ref size))
            {
                return string.Empty;
            }

            return Path.GetFileName(buffer.ToString());
        }
        finally
        {
            NativeMethods.CloseHandle(process);
        }
    }
}
