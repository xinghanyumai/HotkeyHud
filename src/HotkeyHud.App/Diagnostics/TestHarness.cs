#if DEBUG
using HotkeyHud.App.Platform;

namespace HotkeyHud.App.Diagnostics;

/// <summary>
/// 验收套件的公共夹具（S8 前置 2）。
/// <para>
/// 所有需要抢前台的套件都会在 <c>SetForegroundWindow</c> 上偶发失败：上一个套件退出后
/// 前台还没稳定，下一个就开始抢。**不用固定 Sleep**——慢机器上照样会红，还会掩盖真实问题。
/// 这里改成「观察前台连续若干次不变才算稳定」，并允许失败后重试一次。
/// </para>
/// </summary>
internal static class TestHarness
{
    private const int StableSamples = 3;
    private const int PollMs = 15;

    /// <summary>前台句柄连续 <see cref="StableSamples"/> 次不变即认为稳定；最多等 <paramref name="timeoutMs"/>。</summary>
    public static void WaitForStableForeground(int timeoutMs = 1000)
    {
        IntPtr last = IntPtr.Zero;
        int stable = 0;
        long deadline = Environment.TickCount64 + timeoutMs;

        while (Environment.TickCount64 < deadline)
        {
            IntPtr current = NativeMethods.GetForegroundWindow();
            if (current != IntPtr.Zero && current == last)
            {
                if (++stable >= StableSamples)
                {
                    return;
                }
            }
            else
            {
                last = current;
                stable = 0;
            }

            Application.DoEvents();
            Thread.Sleep(PollMs);
        }
    }

    /// <summary>
    /// 跑一次用例；失败就等前台稳定后重试一次。
    /// 返回是否成功，并把「是否重试过」写进 <paramref name="detail"/> 供报告使用。
    /// </summary>
    public static bool TryRun(Func<string> body, out string detail, out bool retried)
    {
        retried = false;

        try
        {
            detail = body();
            return true;
        }
        catch (Exception first)
        {
            WaitForStableForeground();
            retried = true;

            try
            {
                detail = body();
                return true;
            }
            catch (Exception second)
            {
                detail = $"首次失败：{first.Message}；重试后仍失败：{second.Message}";
                return false;
            }
        }
    }
}
#endif
