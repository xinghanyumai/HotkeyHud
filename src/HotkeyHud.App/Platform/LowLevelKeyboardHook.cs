using System.Runtime.InteropServices;

namespace HotkeyHud.App.Platform;

/// <summary>
/// 低层键盘钩子，**只用来吞掉翻页键**（§2.3）。
/// <para>
/// 三条纪律：
/// ① 只对 <see cref="ShouldSwallow"/> 返回 true 的键返回 1，其余一律 <c>CallNextHookEx</c>；
/// ② 回调里不做 I/O、不分配、不查仓库——只有一次委托调用与一个整数比较；
/// ③ 不常驻：只在「多页且面板可见」期间安装，收起 / 超时 / 退出都要立刻卸。
/// </para>
/// <para>
/// 委托必须存在字段里：交给 GC 回收后系统回调会踩空指针。
/// </para>
/// </summary>
public sealed class LowLevelKeyboardHook : IDisposable
{
    private readonly Func<int, bool> _shouldSwallow;
    private readonly Action<int>? _onSwallow;
    private readonly NativeMethods.LowLevelKeyboardProc _callback;
    private IntPtr _hook;

    public LowLevelKeyboardHook(Func<int, bool> shouldSwallow, Action<int>? onSwallow = null)
    {
        _shouldSwallow = shouldSwallow ?? throw new ArgumentNullException(nameof(shouldSwallow));
        _onSwallow = onSwallow;
        _callback = OnKeyboard;
    }

    public bool IsInstalled => _hook != IntPtr.Zero;

    public void Install()
    {
        if (_hook != IntPtr.Zero)
        {
            return;
        }

        IntPtr module = NativeMethods.GetModuleHandleW(null);
        _hook = NativeMethods.SetWindowsHookExW(NativeMethods.WH_KEYBOARD_LL, _callback, module, 0);
        if (_hook == IntPtr.Zero)
        {
            throw new InvalidOperationException(
                $"SetWindowsHookEx 失败，Win32 错误码 {Marshal.GetLastWin32Error()}");
        }
    }

    public void Uninstall()
    {
        if (_hook == IntPtr.Zero)
        {
            return;
        }

        NativeMethods.UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
    }

    private IntPtr OnKeyboard(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            // KBDLLHOOKSTRUCT.vkCode 在偏移 0
            int virtualKey = Marshal.ReadInt32(lParam);
            if (_shouldSwallow(virtualKey))
            {
                _onSwallow?.Invoke(virtualKey);
                return 1; // 吞掉：不再传给后续钩子与应用
            }
        }

        return NativeMethods.CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    public void Dispose() => Uninstall();
}
