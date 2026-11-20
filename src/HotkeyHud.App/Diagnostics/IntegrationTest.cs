#if DEBUG
using System.Diagnostics;
using System.Text;
using HotkeyHud.App.Platform;
using HotkeyHud.Core;

namespace HotkeyHud.App.Diagnostics;

/// <summary>
/// S2.5 集成验收：用 <c>keybd_event</c> 注入真实按键，走 Raw Input → 状态机 → 覆盖窗全链路。
/// 这里没有「测试专用分支」，全部通过 HudApp 的公开状态观察。
/// <para>
/// 注意：<c>Thread.Sleep</c> 只用于让出 CPU 给消息泵，状态机推进完全靠 20ms 计时器，
/// 不靠 sleep 计时。
/// </para>
/// </summary>
internal static class IntegrationTest
{
    private const int VkControl = 0x11;
    private const int VkC = 0x43;
    private const int VkB = 0x42;
    private const int VkK = 0x4B;
    private const int IdleSampleMs = 10_000;
    private const int CpuBudgetMs = 100;

    private static readonly List<string> Report = new();
    private static int _passed;
    private static int _failures;

    public static int Run()
    {
        ApplicationConfiguration.Initialize();
        TestHarness.WaitForStableForeground(); // 上一个套件刚退出时前台可能还在变
        try
        {
            Console.OutputEncoding = new UTF8Encoding(false);
        }
        catch
        {
            // 无控制台时忽略
        }

        using var target = CreateWindow("HotkeyHud 集成测试窗口", new Point(40, 40));
        using var other = CreateWindow("HotkeyHud 第二窗口", new Point(520, 40));

        string knownRepo = CreateRepoDirectory(withProfile: true);
        string emptyRepo = CreateRepoDirectory(withProfile: false);

        try
        {
            RunTimingCases(target, other, knownRepo);
            RunUnknownAppCase(target, emptyRepo);
            RunSuppressedAndIdleCases(target, emptyRepo);
        }
        finally
        {
            TryDeleteDirectory(knownRepo);
            TryDeleteDirectory(emptyRepo);
        }

        return Flush();
    }

    // ---------------- 用例 1–6：长按、短按、熟记、前台切换 ----------------

    private static void RunTimingCases(Form target, Form other, string repoDirectory)
    {
        using var hud = new HudApp(repoDirectory);
        ReleaseAll();
        FocusWindow(target.Handle);

        Case("Case01 按住 100ms 面板不出现", () =>
        {
            ReleaseAll();
            FocusWindow(target.Handle);
            KeyDown(VkControl);
            Pump(100);
            Require(hud.State == HudState.Armed,
                $"按下 Ctrl 后状态机未进入 Armed（State={hud.State}）——输入事件未到达");
            Require(!hud.IsOverlayVisible, "100ms 时面板已经出现，300ms 阈值未生效");
            return $"State={hud.State}，可见=False";
        });

        Case("Case02 按住 550ms 面板出现且标题正确", () =>
        {
            Pump(450);
            Require(hud.IsOverlayVisible, "累计 550ms 面板仍未出现");
            Require(
                hud.CurrentLabel.Contains("HudTestApp", StringComparison.Ordinal),
                $"label 未包含应用名：\"{hud.CurrentLabel}\"");
            Require(
                hud.CurrentLabel.Contains("条", StringComparison.Ordinal),
                $"label 未包含条目数：\"{hud.CurrentLabel}\"");
            return $"label=\"{hud.CurrentLabel}\"";
        });

        Case("Case03 松开触发键面板消失", () =>
        {
            KeyUp(VkControl);
            Pump(150);
            Require(!hud.IsOverlayVisible, "松开 Ctrl 后面板仍在");
            return $"可见={hud.IsOverlayVisible}";
        });

        Case("Case04 短按组合全程不弹面板", () =>
        {
            ReleaseAll();
            FocusWindow(target.Handle);

            bool everVisible = false;
            KeyDown(VkControl);
            everVisible |= PumpTracking(60, hud);
            KeyDown(VkC);

            // 关键：按下 C 之后 Ctrl 继续按住 400ms，累计 460ms 已远超 300ms 阈值。
            // 若「非修饰键取消 Armed」失效，面板必然在这段里出现——这样才不是重复 Case03。
            everVisible |= PumpTracking(560, hud);

            KeyUp(VkC);
            KeyUp(VkControl);
            everVisible |= PumpTracking(150, hud);

            Require(!everVisible, "Ctrl 按住超过阈值后面板出现，说明按 C 没有取消 Armed");
            return "Ctrl 累计按住 620ms，面板从未出现";
        });

        Case("Case05 熟记组合与普通组合同样高亮（v1.7 降权不剔除）", () =>
        {
            PressAndArm(hud, target.Handle);
            PumpTracking(550, hud);
            Require(hud.IsOverlayVisible,
                $"前置条件不成立：面板未显示（State={hud.State}，计时器={hud.TimerRunning}）");

            // 熟记组合 Ctrl+B：v1.7 起不再「立即收起」，走与普通组合相同的高亮路径
            var before = PanelRect(hud);
            KeyDown(VkB);
            Pump(80);
            KeyUp(VkB);

            Require(hud.IsOverlayVisible, "按 Ctrl+B 后面板应收起为高亮态，而不是消失");
            Require(hud.HighlightedCombo == new Combo(Modifier.Ctrl, VkB),
                $"高亮组合不符：{hud.HighlightedCombo?.ToString() ?? "null"}");

            var afterKnown = PanelRect(hud);
            Require(before == afterKnown,
                $"熟记组合高亮改变了面板尺寸：{before} → {afterKnown}（会重建 DIB 并闪烁）");

            Pump(550);
            Require(!hud.IsOverlayVisible, "闪烁 500ms 后面板应自动收起");
            KeyUp(VkControl);
            Pump(150);

            // 非熟记组合 Ctrl+K：行为与上面完全一致
            PressAndArm(hud, target.Handle);
            PumpTracking(550, hud);
            Require(hud.IsOverlayVisible, "前置条件不成立：面板未重新显示");

            var beforeK = PanelRect(hud);
            KeyDown(VkK);
            Pump(80);
            KeyUp(VkK);

            Require(hud.IsOverlayVisible, "按 Ctrl+K 后面板应收起为高亮态，而不是消失");
            Require(hud.HighlightedCombo == new Combo(Modifier.Ctrl, VkK),
                $"高亮组合不符：{hud.HighlightedCombo?.ToString() ?? "null"}");

            var afterK = PanelRect(hud);
            Require(beforeK == afterK,
                $"高亮改变了面板尺寸：{beforeK} → {afterK}（会重建 DIB 并闪烁）");

            Pump(550);
            Require(!hud.IsOverlayVisible, "闪烁 500ms 后面板应自动收起");
            KeyUp(VkControl);
            Pump(150);

            return $"Ctrl+B / Ctrl+K 均高亮 {afterK.Width}×{afterK.Height} 尺寸不变，500ms 后收起";
        });

        Case("Case06 前台切换立即收起面板", () =>
        {
            PressAndArm(hud, target.Handle);
            PumpTracking(550, hud);
            Require(hud.IsOverlayVisible,
                $"前置条件不成立：面板未显示（State={hud.State}，计时器={hud.TimerRunning}）");

            FocusWindow(other.Handle);
            Pump(200);

            Require(!hud.IsOverlayVisible, "前台切换后面板仍在");
            KeyUp(VkControl);
            Pump(150);
            return "切换后 200ms 内已收起";
        });
    }

    // ---------------- 用例 7：未收录应用 ----------------

    private static void RunUnknownAppCase(Form target, string emptyRepoDirectory)
    {
        using var hud = new HudApp(emptyRepoDirectory);
        ReleaseAll();
        FocusWindow(target.Handle);

        Case("Case07 未收录应用显示「未收录」", () =>
        {
            PressAndArm(hud, target.Handle);
            PumpTracking(550, hud);
            bool shown = hud.IsOverlayVisible;
            string label = hud.CurrentLabel;
            KeyUp(VkControl);
            Pump(150);

            Require(shown, "面板未出现");
            Require(label.StartsWith("未收录", StringComparison.Ordinal),
                $"label 不是「未收录 …」：\"{label}\"");
            return $"label=\"{label}\"";
        });
    }

    // ---------------- 用例 8–10：抑制、空闲 CPU、S1 属性 ----------------

    private static void RunSuppressedAndIdleCases(Form target, string emptyRepoDirectory)
    {
        using var hud = new HudApp(emptyRepoDirectory);
        ReleaseAll();
        FocusWindow(target.Handle);

        Case("Case08 手动暂停时长按不弹面板", () =>
        {
            hud.Suppressed = true;
            long receivedBefore = hud.Input.ReceivedCount;

            KeyDown(VkControl);
            bool everVisible = PumpTracking(400, hud);
            long receivedAfter = hud.Input.ReceivedCount;

            KeyUp(VkControl);
            Pump(150);
            hud.Suppressed = false;

            // 没有这一步，「面板未出现」可能只是输入根本没到达——断言会变成恒真
            Require(receivedAfter > receivedBefore,
                "暂停期间没有收到任何 Raw Input 事件——输入未到达，本用例无意义");
            Require(!everVisible, "手动暂停期间面板仍然出现");
            return $"期间收到 {receivedAfter - receivedBefore} 个输入事件，面板从未出现";
        });

        Case("Case09 空闲 10 秒 CPU 增量 < 100ms", () =>
        {
            using var process = Process.GetCurrentProcess();
            process.Refresh();
            TimeSpan cpuBefore = process.TotalProcessorTime;

            var clock = Stopwatch.StartNew();
            while (clock.ElapsedMilliseconds < IdleSampleMs)
            {
                Thread.Sleep(50);
                Application.DoEvents();
            }

            process.Refresh();
            double deltaMs = (process.TotalProcessorTime - cpuBefore).TotalMilliseconds;

            Require(!hud.TimerRunning, "空闲时计时器仍在运行");
            Require(deltaMs < CpuBudgetMs,
                $"空闲 CPU 增量 {deltaMs:F1} ms，超过 {CpuBudgetMs} ms 预算");
            return $"CPU 增量 {deltaMs:F1} ms，计时器已停";
        });

        Case("Case10 S1 窗口属性保持不变", () =>
        {
            long exStyle = NativeMethods.GetWindowLongPtr(
                hud.Overlay.Handle, NativeMethods.GWL_EXSTYLE).ToInt64();

            bool layered = (exStyle & NativeMethods.WS_EX_LAYERED) != 0;
            bool transparent = (exStyle & NativeMethods.WS_EX_TRANSPARENT) != 0;
            bool noActivate = (exStyle & NativeMethods.WS_EX_NOACTIVATE) != 0;
            bool toolWindow = (exStyle & NativeMethods.WS_EX_TOOLWINDOW) != 0;

            Require(layered && transparent && noActivate && toolWindow,
                $"S1 窗口属性被破坏：LAYERED={layered} TRANSPARENT={transparent} "
                + $"NOACTIVATE={noActivate} TOOLWINDOW={toolWindow}");
            return "LAYERED|TRANSPARENT|NOACTIVATE|TOOLWINDOW 全置位";
        });
    }

    // ---------------- 基础设施 ----------------

    private static void Case(string name, Func<string> body)
    {
        // 抢前台的套件会偶发失败：等前台稳定后重试一次，并在报告里标出来
        bool ok = TestHarness.TryRun(body, out string detail, out bool retried);
        string suffix = retried ? "（重试 1 次）" : string.Empty;

        if (ok)
        {
            _passed++;
            Report.Add($"[PASS] {name} — {detail}{suffix}");
        }
        else
        {
            _failures++;
            Report.Add($"[FAIL] {name} — {detail}{suffix}");
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new IntegrationCheckException(message);
        }
    }

    private static Form CreateWindow(string title, Point location)
    {
        var form = new Form
        {
            Text = title,
            StartPosition = FormStartPosition.Manual,
            Location = location,
            Size = new Size(420, 160),
            FormBorderStyle = FormBorderStyle.FixedToolWindow,
            ShowInTaskbar = true,
        };

        form.Show();
        form.Activate();
        return form;
    }

    private static void FocusWindow(IntPtr handle)
    {
        NativeMethods.SetForegroundWindow(handle);
        Pump(80);

        IntPtr current = NativeMethods.GetForegroundWindow();
        if (current != handle)
        {
            throw new IntegrationCheckException(
                $"无法把目标窗口切到前台（当前前台 = 0x{current.ToInt64():X}），集成测试需要独占前台");
        }
    }

    private static string CreateRepoDirectory(bool withProfile)
    {
        string directory = Path.Combine(
            Path.GetTempPath(), "hotkeyhud-integration-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);

        if (!withProfile)
        {
            return directory;
        }

        string processName = Process.GetCurrentProcess().ProcessName;
        string json = $$"""
            {
              "app": "hudtest",
              "displayName": "HudTestApp",
              "processNames": ["{{processName}}.EXE"],
              "shortcuts": [
                { "keys": "Ctrl+K", "action": "测试动作 K" },
                { "keys": "Ctrl+J", "action": "测试动作 J" },
                { "keys": "Ctrl+B", "action": "加粗（熟记名单内，v1.7 降权到通用组）" }
              ]
            }
            """;

        File.WriteAllText(Path.Combine(directory, "hudtest.json"), json);
        return directory;
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch
        {
            // 临时目录清理失败不影响结论
        }
    }

    /// <summary>
    /// 先松开所有键、确保前台，再按下 Ctrl 并确认状态机真的进入了 Armed。
    /// 把「输入事件没到」与「计时器没跑」分开，避免偶发失败只能靠猜。
    /// </summary>
    private static void PressAndArm(HudApp hud, IntPtr targetHandle)
    {
        ReleaseAll();
        Require(hud.State == HudState.Idle && !hud.TimerRunning,
            $"松开按键后状态机未回到 Idle（State={hud.State}，计时器={hud.TimerRunning}）"
            + "——上一个用例的按键状态污染了本用例");

        FocusWindow(targetHandle);
        KeyDown(VkControl);
        Pump(100);
        Require(hud.State is HudState.Armed or HudState.Visible,
            $"按下 Ctrl 后状态机未进入 Armed（State={hud.State}）——输入事件未到达");
    }

    private static (int Left, int Top, int Width, int Height) PanelRect(HudApp hud)
    {
        NativeMethods.GetWindowRect(hud.Overlay.Handle, out NativeMethods.RECT rect);
        return (rect.Left, rect.Top, rect.Width, rect.Height);
    }

    private static void KeyDown(int virtualKey)
        => NativeMethods.keybd_event((byte)virtualKey, 0, 0, UIntPtr.Zero);

    private static void KeyUp(int virtualKey)
        => NativeMethods.keybd_event((byte)virtualKey, 0, NativeMethods.KEYEVENTF_KEYUP, UIntPtr.Zero);

    private static void ReleaseAll()
    {
        KeyUp(VkC);
        KeyUp(VkK);
        KeyUp(VkControl);
        Pump(80);
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

    /// <summary>推进指定时长，并返回期间面板是否曾经出现过（用于「全程不弹」类断言）。</summary>
    private static bool PumpTracking(int milliseconds, HudApp hud)
    {
        bool everVisible = hud.IsOverlayVisible;
        long deadline = Environment.TickCount64 + milliseconds;
        while (Environment.TickCount64 < deadline)
        {
            Application.DoEvents();
            if (hud.IsOverlayVisible)
            {
                everVisible = true;
            }

            Thread.Sleep(2);
        }

        return everVisible || hud.IsOverlayVisible;
    }

    private static int Flush()
    {
        Report.Add($"--- 通过 {_passed}，失败 {_failures} ---");

        string text = string.Join(Environment.NewLine, Report);
        Console.WriteLine(text);
        try
        {
            File.WriteAllText(
                Path.Combine(Path.GetTempPath(), "hotkeyhud-integration.log"),
                text + Environment.NewLine);
        }
        catch
        {
            // 日志失败不影响退出码
        }

        return _failures;
    }

    private sealed class IntegrationCheckException : Exception
    {
        public IntegrationCheckException(string message) : base(message)
        {
        }
    }
}
#endif
