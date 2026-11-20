using System.Runtime.InteropServices;

namespace HotkeyHud.App.Platform;

/// <summary>
/// 低层键盘钩子，**只用来吞掉翻页键**（§2.3）。
/// <para>
/// 四条纪律：
/// ① 只对 <see cref="ShouldSwallow"/> 返回 true 的键返回 1，其余一律 <c>CallNextHookEx</c>；
/// ② 回调里不做 I/O、不分配、不查仓库——只有一次委托调用与一个整数比较；
/// ③ 不常驻：只在「多页且面板可见」期间安装，收起 / 超时 / 退出都要立刻卸；
/// ④ **按下与松开都吞，但只有按下算一次翻页**——<c>KBDLLHOOKSTRUCT.flags</c> 的
/// <c>LLKHF_UP</c> 位区分两者，漏看它会让一次 ↓ 翻两页。
/// </para>
/// <para>
/// 委托必须存在字段里：交给 GC 回收后系统回调会踩空指针。
/// </para>
/// </summary>
public sealed class LowLevelKeyboardHook : IDisposable
{
    /// <summary><c>KBDLLHOOKSTRUCT.vkCode</c> 在偏移 0。</summary>
    private const int VkCodeOffset = 0;

    /// <summary><c>KBDLLHOOKSTRUCT.flags</c> 在偏移 8（0 是 vkCode，4 是 scanCode）。</summary>
    private const int FlagsOffset = 8;

    /// <summary><c>LLKHF_UP</c>：置位表示这是一次「松开」。</summary>
    private const int LlkhfUp = 0x80;

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
            int virtualKey = Marshal.ReadInt32(lParam, VkCodeOffset);
            if (_shouldSwallow(virtualKey))
            {
                int flags = Marshal.ReadInt32(lParam, FlagsOffset);
                bool isKeyUp = (flags & LlkhfUp) != 0;

                // 松开也必须吞：否则前台应用会收到一个孤立的 keyup（Word 光标会动）。
                // 但只有「按下」才算一次翻页意图——之前没看 flags，
                // 一次 ↓ 会翻两页（按下一次、松开又一次）。
                if (!isKeyUp)
                {
                    _onSwallow?.Invoke(virtualKey);
                }

                return 1;
            }
        }

        return NativeMethods.CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    /// <summary>
    /// 用合成的 <c>KBDLLHOOKSTRUCT</c> 走一遍真实解析逻辑（偏移与 flags 位）。
    /// 真机注入时低层钩子不一定回调（取决于环境），所以把这段逻辑做成可直接喂结构体的入口。
    /// </summary>
    internal IntPtr DispatchForTest(int nCode, IntPtr wParam, IntPtr lParam) => OnKeyboard(nCode, wParam, lParam);

    public void Dispose() => Uninstall();

}
