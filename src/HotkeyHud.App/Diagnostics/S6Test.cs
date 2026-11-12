#if DEBUG
using System.Text;
using HotkeyHud.App.Platform;
using HotkeyHud.App.UI;
using HotkeyHud.Core;

namespace HotkeyHud.App.Diagnostics;

/// <summary>
/// S6 打磨验收：混合 DPI 的定位逻辑、上缘翻转、淡入。
/// <para>
/// 混合 DPI 分两层验：定位/缩放是纯函数，用合成双屏布局断言；真机截图需要第二块屏，
/// 本机只有一块，因此「副屏截图」这条明确记为未验证。
/// </para>
/// </summary>
internal static class S6Test
{
    private const int FadeBudgetFrames = 6; // 120ms / 16ms ≈ 8 帧，留一点余量

    private static readonly List<string> Report = new();
    private static int _passed;
    private static int _failures;

    public static int Run(string outputDirectory)
    {
        Directory.CreateDirectory(outputDirectory);

        ApplicationConfiguration.Initialize();
        try
        {
            Console.OutputEncoding = new UTF8Encoding(false);
        }
        catch
        {
            // 无控制台时忽略
        }

        Case("S6-1 混合 DPI：按光标挑屏与取缩放", MixedDpiLogic);
        Case("S6-2 上缘翻转", () => TopEdgeFlip(outputDirectory));
        Case("S6-3 淡入 120ms", FadeIn);
        Case("S6-4 关闭动画后无淡入", FadeDisabled);
        Case("S6-5 隐藏立即生效", HideIsImmediate);
        Case("S6-6 本机显示器数量", MonitorInventory);

        return Flush();
    }

    // ---------------- 用例 ----------------

    private static string MixedDpiLogic()
    {
        // 合成布局：主屏 100%，副屏 150% 且更宽
        var primary = new MonitorInfo(
            new IntPtr(1), new Rectangle(0, 0, 1920, 1080), new Rectangle(0, 0, 1920, 1040), 96);
        var secondary = new MonitorInfo(
            new IntPtr(2), new Rectangle(1920, 0, 2560, 1440), new Rectangle(1920, 0, 2560, 1400), 144);
        MonitorInfo[] monitors = { primary, secondary };

        MonitorInfo onPrimary = ScreenAnchor.Pick(monitors, new Point(100, 100));
        Require(onPrimary.Dpi == 96, $"光标在主屏时应取 96 DPI，实际 {onPrimary.Dpi}");

        MonitorInfo onSecondary = ScreenAnchor.Pick(monitors, new Point(2000, 100));
        Require(onSecondary.Dpi == 144, $"光标在副屏时应取 144 DPI，实际 {onSecondary.Dpi}");
        Require(Math.Abs(onSecondary.Scale - 1.5f) < 0.001f,
            $"副屏缩放应为 1.5，实际 {onSecondary.Scale}");

        // 尺寸按副屏缩放算：540 逻辑宽 → 810 物理宽
        var size = new Size(810, 372);

        // 光标在副屏下半部 → 贴上缘，边距 40 × 1.5 = 60
        Rectangle top = ScreenAnchor.Anchor(secondary, size, new Point(2500, 1300));
        Require(top.Y == secondary.WorkArea.Y + 60,
            $"下半屏应贴上缘（y=60），实际 y={top.Y}");
        Require(top.X == secondary.WorkArea.X + (2560 - 810) / 2,
            $"应水平居中于副屏，实际 x={top.X}");
        Require(secondary.WorkArea.Contains(top), "面板越出了副屏工作区");

        // 光标在副屏上半部 → 贴下缘
        Rectangle bottom = ScreenAnchor.Anchor(secondary, size, new Point(2500, 100));
        Require(bottom.Y == secondary.WorkArea.Bottom - size.Height - 60,
            $"上半屏应贴下缘（y={secondary.WorkArea.Bottom - size.Height - 60}），实际 y={bottom.Y}");

        return $"副屏 150%：尺寸按 1.5 缩放、边距 60；上缘 y={top.Y}、下缘 y={bottom.Y}";
    }

    private static string TopEdgeFlip(string outputDirectory)
    {
        Rectangle workArea = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1920, 1040);
        using var overlay = new OverlayForm(() => false); // 本用例只验位置，关掉动画免干扰
        HudModel model = BuildModel();

        Point original = Cursor.Position;
        try
        {
            // 光标在上半部 → 贴下缘
            SetCursor(new Point(workArea.X + workArea.Width / 2, workArea.Y + workArea.Height / 4));
            overlay.ShowModel(model, null);
            Pump(200);
            int lower = overlay.LastBounds.Y;
            Capture(overlay, Path.Combine(outputDirectory, "s6-anchor-bottom.png"));

            // 光标在下半部 → 贴上缘
            SetCursor(new Point(workArea.X + workArea.Width / 2, workArea.Y + workArea.Height * 3 / 4));
            bool moved = overlay.RefreshAnchor();
            Pump(200);
            int upper = overlay.LastBounds.Y;
            Capture(overlay, Path.Combine(outputDirectory, "s6-anchor-top.png"));

            Require(moved, "光标换到下半屏后 RefreshAnchor 没有报告位置变化");
            Require(lower > workArea.Y + workArea.Height / 2,
                $"光标在上半屏时应贴下缘，实际 y={lower}（工作区 {workArea}）");
            Require(upper < workArea.Y + workArea.Height / 2,
                $"光标在下半屏时应贴上缘，实际 y={upper}");

            return $"上半屏 → y={lower}（下缘）；下半屏 → y={upper}（上缘）";
        }
        finally
        {
            SetCursor(original);
            overlay.HideOverlay();
        }
    }

    private static string FadeIn()
    {
        using var overlay = new OverlayForm(() => true);
        overlay.ShowModel(BuildModel(), null);

        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (clock.ElapsedMilliseconds < 260)
        {
            Application.DoEvents();
            Thread.Sleep(2);
        }

        int frames = overlay.FadeFramesPushed;
        overlay.HideOverlay();

        Require(frames >= FadeBudgetFrames,
            $"淡入只推了 {frames} 帧，少于 {FadeBudgetFrames}（120ms / 16ms ≈ 8 帧）");
        return $"推了 {frames} 帧（120ms / 16ms 一帧，ease-out）";
    }

    private static string FadeDisabled()
    {
        using var overlay = new OverlayForm(() => false);
        overlay.ShowModel(BuildModel(), null);
        Pump(150);

        int frames = overlay.FadeFramesPushed;
        overlay.HideOverlay();

        Require(frames == 1, $"关闭动画后应只推 1 帧，实际 {frames} 帧");
        return "关闭动画后只推 1 帧（直接显示）";
    }

    private static string HideIsImmediate()
    {
        using var overlay = new OverlayForm(() => true);
        overlay.ShowModel(BuildModel(), null);
        Pump(200); // 等淡入结束

        overlay.HideOverlay();

        // 不 pump：隐藏必须同步生效
        bool visible = NativeMethods.IsWindowVisible(overlay.Handle);
        Require(!visible, "HideOverlay 之后窗口仍然可见（隐藏被做成了动画）");
        return "HideOverlay 后同步不可见，无淡出";
    }

    private static string MonitorInventory()
    {
        Screen[] screens = Screen.AllScreens;
        string detail = string.Join("；", screens.Select(s =>
            $"{s.DeviceName} {s.Bounds.Width}×{s.Bounds.Height}{(s.Primary ? "（主）" : "")}"));

        return screens.Length >= 2
            ? $"{screens.Length} 块屏：{detail}"
            : $"{screens.Length} 块屏：{detail} —— **副屏截图未验证**（本机只有一块屏）";
    }

    // ---------------- 基础设施 ----------------

    private static HudModel BuildModel()
    {
        var repository = new ShortcutRepository(
            Path.Combine(AppContext.BaseDirectory, "Data", "apps"));
        var known = new KnownShortcutFilter();
        return HudBuilder.Build(repository.FindByProcess("chrome.exe"), Modifier.Ctrl, known, "Chrome");
    }

    /// <summary>截工作区：面板贴在上缘还是下缘，一张图就能看出来。</summary>
    private static void Capture(OverlayForm overlay, string path)
    {
        Rectangle area = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1920, 1040);
        ScreenCapture.CaptureTo(area, path);
        _ = overlay;
    }

    private static void SetCursor(Point point)
    {
        Cursor.Position = point;
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
            throw new S6CheckException(message);
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
                Path.Combine(Path.GetTempPath(), "hotkeyhud-s6.log"), text + Environment.NewLine);
        }
        catch
        {
            // 日志失败不影响退出码
        }

        return _failures;
    }

    private sealed class S6CheckException : Exception
    {
        public S6CheckException(string message) : base(message)
        {
        }
    }
}
#endif
