#if DEBUG
using System.Diagnostics;
using System.Text;
using HotkeyHud.App.Platform;

namespace HotkeyHud.App.Diagnostics;

/// <summary>
/// §2.3 的前提实验：<c>WH_KEYBOARD_LL</c> 返回 1 吞掉按键后，Raw Input 是否仍收到该键？
/// <para>
/// 仍收到 → 钩子只负责吞、翻页交给 Raw Input → 状态机，干净。
/// 收不到 → 必须在钩子里 PostMessage 发翻页命令，状态机忽略该键的 Raw Input。
/// </para>
/// 这个结论决定整个翻页通路的形状，所以先单独验，不靠猜。
/// </summary>
internal static class HookProbe
{
    private const int VkUp = 0x26;
    private const int VkDown = 0x28;

    public static int Run()
    {
        ApplicationConfiguration.Initialize();
        try
        {
            Console.OutputEncoding = new UTF8Encoding(false);
        }
        catch
        {
            // 无控制台时忽略
        }

        using var input = new RawInputSource();
        var rawCounts = new Dictionary<int, int>();
        input.Input += e =>
        {
            if (!e.IsDown)
            {
                return;
            }

            rawCounts[e.Code] = rawCounts.GetValueOrDefault(e.Code) + 1;
        };

        Pump(200); // 让 Raw Input 注册生效

        var lines = new List<string>();

        // ---- 对照组：不装钩子 ----
        rawCounts.Clear();
        Inject(VkUp);
        Pump(250);
        int withoutHook = rawCounts.GetValueOrDefault(VkUp);
        lines.Add($"不装钩子：注入 VK_UP 1 次 → Raw Input 收到 {withoutHook} 次");

        // ---- 实验组：装钩子并吞掉 ----
        int hookHits = 0;
        using var hook = new LowLevelKeyboardHook(ShouldSwallow, _ => hookHits++);
        hook.Install();
        Pump(200);

        rawCounts.Clear();
        hookHits = 0;
        Inject(VkUp);
        Pump(250);
        int withHook = rawCounts.GetValueOrDefault(VkUp);
        lines.Add($"装钩子且吞掉：钩子回调 {hookHits} 次 → Raw Input 收到 {withHook} 次");

        hook.Uninstall();

        // ---- 结论 ----
        string verdict;
        bool rawStillArrives = withHook > 0;
        if (withoutHook == 0)
        {
            verdict = "前提不成立：连对照组都没收到 Raw Input，注入通路有问题，本次结论无效";
        }
        else if (rawStillArrives)
        {
            verdict = "结论 A：吞掉后 Raw Input 仍收到 → 钩子只负责吞，翻页由 Raw Input → 状态机驱动";
        }
        else
        {
            verdict = "结论 B：吞掉后 Raw Input 收不到 → 必须在钩子里 PostMessage 发翻页命令，"
                      + "状态机忽略该键的 Raw Input";
        }

        lines.Add(verdict);
        lines.Add(rawStillArrives && withoutHook > 0 ? "--- 通过 1，失败 0 ---" : "--- 通过 0，失败 1 ---");

        string text = string.Join(Environment.NewLine, lines);
        Console.WriteLine(text);
        try
        {
            File.WriteAllText(
                Path.Combine(Path.GetTempPath(), "hotkeyhud-hookprobe.log"),
                text + Environment.NewLine);
        }
        catch
        {
            // 日志失败不影响退出码
        }

        return rawStillArrives && withoutHook > 0 ? 0 : 1;
    }

    private static bool ShouldSwallow(int virtualKey) => virtualKey is VkUp or VkDown;

    private static void Inject(int virtualKey)
    {
        NativeMethods.keybd_event((byte)virtualKey, 0, 0, UIntPtr.Zero);
        Pump(30);
        NativeMethods.keybd_event((byte)virtualKey, 0, NativeMethods.KEYEVENTF_KEYUP, UIntPtr.Zero);
    }

    private static void Pump(int milliseconds)
    {
        long deadline = Environment.TickCount64 + milliseconds;
        while (Environment.TickCount64 < deadline)
        {
            Application.DoEvents();
            Thread.Sleep(2);
        }
    }
}
#endif
