#if DEBUG
using System.Diagnostics;
using System.Text;
using HotkeyHud.App.Platform;
using HotkeyHud.Core;

namespace HotkeyHud.App.Diagnostics;

/// <summary>
/// S4 真机验证：用一个铺满显示器的无边框窗口模拟「全屏应用」，验证抑制链路
/// 采集 → 判定 → 状态机 Suppressed → 不弹面板。
/// <para>
/// 只做真机做不到的那部分自动化（开窗口、注入按键、截图、计时）；
/// 真正的独占全屏 / 演示模式需要人工开视频或 PPT，见 README。
/// </para>
/// </summary>
internal static class SuppressionTest
{
    private const int VkControl = 0x11;
    private const int IdleSampleMs = 10_000;
    private const int CpuBudgetMs = 100;
    private const int CaptureSamples = 100;
    private const double CaptureBudgetMs = 5.0;

    private static readonly List<string> Report = new();
    private static int _passed;
    private static int _failures;

    public static int Run(string outputDirectory)
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

        Directory.CreateDirectory(outputDirectory);

        Case("S4-1 普通窗口不抑制", () => NormalWindowShows(outputDirectory));
        Case("S4-2 无边框全屏抑制", () => BorderlessFullscreenSuppresses(outputDirectory));
        Case("S4-3 切回普通窗口后恢复", () => RecoversAfterLeavingFullscreen(outputDirectory));
        Case("S4-4 extraGameProcesses 命中 D3", () => ExtraProcessListTriggersD3(outputDirectory));
        Case("S4-5 单次采集 p95 < 5ms", MeasureCaptureCost);
        Case("S4-6 空闲 10 秒 CPU 增量 < 100ms", MeasureIdleCpu);

        return Flush();
    }

    // ---------------- 场景 ----------------

    private static string NormalWindowShows(string outputDirectory)
    {
        using Form normal = CreateWindow("HotkeyHud 普通窗口", new Rectangle(40, 40, 520, 200));
        using var hud = new HudApp();

        ReleaseAll();
        FocusWindow(normal.Handle);
        bool shown = LongPressShows(hud, normal.Handle,
            Path.Combine(outputDirectory, "s4-normal-shown.png"));
        ReleaseAll();

        Require(shown, $"普通窗口长按未弹出（GameSuppressed={hud.GameSuppressed}，"
                       + $"原因=\"{hud.SuppressReason}\"）");
        return $"面板正常弹出，GameSuppressed={hud.GameSuppressed}";
    }

    private static string BorderlessFullscreenSuppresses(string outputDirectory)
    {
        using Form normal = CreateWindow("HotkeyHud 普通窗口", new Rectangle(40, 40, 520, 200));
        using Form fullscreen = CreateFullscreenWindow();
        using var hud = new HudApp();

        ReleaseAll();
        FocusWindow(fullscreen.Handle);
        Pump(200);

        int quns = hud.NotificationState;
        bool suppressed = hud.GameSuppressed;
        bool shown = LongPressShows(hud, fullscreen.Handle,
            Path.Combine(outputDirectory, "s4-fullscreen-suppressed.png"));
        ReleaseAll();

        Require(!shown, $"无边框全屏下仍然弹出了面板（GameSuppressed={hud.GameSuppressed}，"
                        + $"原因=\"{hud.SuppressReason}\"）");
        Require(suppressed, $"无边框全屏下未判定为抑制，原因=\"{hud.SuppressReason}\"");

        return $"面板未弹出；QUNS={quns}，命中原因=\"{hud.SuppressReason}\"";
    }

    private static string RecoversAfterLeavingFullscreen(string outputDirectory)
    {
        using Form normal = CreateWindow("HotkeyHud 普通窗口", new Rectangle(40, 40, 520, 200));
        using Form fullscreen = CreateFullscreenWindow();
        using var hud = new HudApp();

        ReleaseAll();
        FocusWindow(fullscreen.Handle);
        Pump(200);
        Require(hud.GameSuppressed, "前置条件不成立：全屏下未抑制");

        // 真实场景里「退出全屏」= 窗口关掉，不是仅仅切走焦点
        fullscreen.Close();
        Pump(200);
        FocusWindow(normal.Handle);

        // QUNS 从「忙」恢复可能有延迟，等它自己降下来（最多 4 秒）
        var wait = Stopwatch.StartNew();
        while (hud.GameSuppressed && wait.ElapsedMilliseconds < 4000)
        {
            Pump(250);
        }

        bool shown = LongPressShows(hud, normal.Handle,
            Path.Combine(outputDirectory, "s4-recovered.png"));
        ReleaseAll();

        Require(shown, $"退出全屏后仍未弹出（GameSuppressed={hud.GameSuppressed}，"
                       + $"原因=\"{hud.SuppressReason}\"，QUNS={hud.NotificationState}，"
                       + $"State={hud.State}，计时器={hud.TimerRunning}，手动暂停={hud.Suppressed}，"
                       + $"等待 {wait.ElapsedMilliseconds} ms）");
        return "退出全屏后抑制解除，面板正常弹出";
    }

    private static string ExtraProcessListTriggersD3(string outputDirectory)
    {
        using Form normal = CreateWindow("HotkeyHud 普通窗口", new Rectangle(40, 40, 520, 200));

        // 把自己的进程名塞进 D3 名单：这是配置项 extraGameProcesses 的端到端验证
        string processName = Process.GetCurrentProcess().ProcessName;
        var rules = new GameRules { GameProcessNames = new[] { processName } };
        using var hud = new HudApp(gameRules: rules);

        ReleaseAll();
        FocusWindow(normal.Handle);
        Pump(200);

        bool suppressed = hud.GameSuppressed;
        string reason = hud.SuppressReason;
        bool shown = LongPressShows(hud, normal.Handle,
            Path.Combine(outputDirectory, "s4-d3-suppressed.png"));
        ReleaseAll();

        Require(suppressed && !shown,
            $"D3 名单未生效：GameSuppressed={suppressed}、面板可见={shown}、原因=\"{reason}\"");
        Require(reason.StartsWith("D3", StringComparison.Ordinal),
            $"命中原因应为 D3，实际 \"{reason}\"");

        return $"名单命中 {processName}，面板未弹出；原因=\"{reason}\"";
    }

    private static string MeasureCaptureCost()
    {
        var probe = new SuppressionProbe();
        probe.Capture(); // 预热

        var samples = new List<double>(CaptureSamples);
        var clock = Stopwatch.StartNew();
        for (int i = 0; i < CaptureSamples; i++)
        {
            // 每次都让缓存失效：测的是最坏情况（含 OpenProcess）
            probe.Invalidate();
            clock.Restart();
            probe.Capture();
            samples.Add(clock.Elapsed.TotalMilliseconds);
        }

        samples.Sort();
        double p95 = samples[(int)((samples.Count - 1) * 0.95)];
        Require(p95 < CaptureBudgetMs, $"单次采集 p95 = {p95:F2} ms，超过 {CaptureBudgetMs} ms");
        return $"p95 = {p95:F2} ms（含 OpenProcess，{samples.Count} 次）";
    }

    private static string MeasureIdleCpu()
    {
        using Form normal = CreateWindow("HotkeyHud 普通窗口", new Rectangle(40, 40, 520, 200));
        using var hud = new HudApp();
        ReleaseAll();
        FocusWindow(normal.Handle);

        using var process = Process.GetCurrentProcess();
        process.Refresh();
        TimeSpan before = process.TotalProcessorTime;

        var clock = Stopwatch.StartNew();
        while (clock.ElapsedMilliseconds < IdleSampleMs)
        {
            Thread.Sleep(50);
            Application.DoEvents();
        }

        process.Refresh();
        double deltaMs = (process.TotalProcessorTime - before).TotalMilliseconds;
        Require(deltaMs < CpuBudgetMs, $"空闲 CPU 增量 {deltaMs:F1} ms，超过 {CpuBudgetMs} ms");
        return $"CPU 增量 {deltaMs:F1} ms（含 2 秒轮询，共 {IdleSampleMs / 2000} 次采集）";
    }

    // ---------------- 基础设施 ----------------

    /// <summary>长按 Ctrl 到超过阈值，返回面板是否出现。截图必须在松开前拍，否则拍到的是已收起的面板。</summary>
    private static bool LongPressShows(HudApp hud, IntPtr target, string? capturePath = null)
    {
        ReleaseAll();
        FocusWindow(target);
        KeyDown(VkControl);
        bool everVisible = PumpTracking(450, hud);

        if (capturePath is not null && hud.IsOverlayVisible)
        {
            CaptureScreen(capturePath);
        }

        KeyUp(VkControl);
        Pump(150);
        return everVisible;
    }

    private static Form CreateWindow(string title, Rectangle bounds)
    {
        var form = new Form
        {
            Text = title,
            StartPosition = FormStartPosition.Manual,
            Bounds = bounds,
            FormBorderStyle = FormBorderStyle.FixedToolWindow,
        };
        form.Show();
        form.Activate();
        Pump(120);
        return form;
    }

    /// <summary>铺满显示器的无边框窗口：CoversWholeMonitor=true、HasCaption=false。</summary>
    private static Form CreateFullscreenWindow()
    {
        Rectangle area = Screen.PrimaryScreen?.Bounds ?? new Rectangle(0, 0, 1920, 1080);
        var form = new Form
        {
            FormBorderStyle = FormBorderStyle.None,
            StartPosition = FormStartPosition.Manual,
            Bounds = area,
            BackColor = Color.FromArgb(0x10, 0x10, 0x14),
            ShowInTaskbar = false,
        };

        form.Show();
        form.Activate();
        NativeMethods.SetForegroundWindow(form.Handle);
        Pump(200);
        return form;
    }

    private static void FocusWindow(IntPtr handle)
    {
        NativeMethods.SetForegroundWindow(handle);
        Pump(120);

        IntPtr current = NativeMethods.GetForegroundWindow();
        if (current != handle)
        {
            throw new SuppressionCheckException(
                $"无法把目标窗口切到前台（当前前台 = 0x{current.ToInt64():X}）");
        }
    }

    private static void CaptureScreen(string path)
    {
        Rectangle area = Screen.PrimaryScreen?.Bounds ?? new Rectangle(0, 0, 1920, 1080);
        ScreenCapture.CaptureTo(area, path);
    }

    private static void KeyDown(int virtualKey)
        => NativeMethods.keybd_event((byte)virtualKey, 0, 0, UIntPtr.Zero);

    private static void KeyUp(int virtualKey)
        => NativeMethods.keybd_event((byte)virtualKey, 0, NativeMethods.KEYEVENTF_KEYUP, UIntPtr.Zero);

    private static void ReleaseAll()
    {
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

    private static void Case(string name, Func<string> body)
    {
        try
        {
            string detail = body();
            _passed++;
            Report.Add($"[PASS] {name} — {detail}");
        }
        catch (Exception ex)
        {
            _failures++;
            Report.Add($"[FAIL] {name} — {ex.Message}");
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new SuppressionCheckException(message);
        }
    }

    private static int Flush()
    {
        Report.Add($"--- 通过 {_passed}，失败 {_failures} ---");

        string text = string.Join(Environment.NewLine, Report);
        Console.WriteLine(text);
        try
        {
            File.WriteAllText(
                Path.Combine(Path.GetTempPath(), "hotkeyhud-suppression.log"),
                text + Environment.NewLine);
        }
        catch
        {
            // 日志失败不影响退出码
        }

        return _failures;
    }

    private sealed class SuppressionCheckException : Exception
    {
        public SuppressionCheckException(string message) : base(message)
        {
        }
    }
}
#endif
