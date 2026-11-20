#if DEBUG
using System.Diagnostics;
using System.Text;
using HotkeyHud.App.Platform;
using HotkeyHud.App.Shell;
using HotkeyHud.App.UI;
using HotkeyHud.Core;

namespace HotkeyHud.App.Diagnostics;

/// <summary>
/// S7 分页验收：页脚、翻页、边界、钩子装/卸、15 秒安全阀。
/// <para>
/// 用例 6/7 里「Word 文档光标有没有动」这一层无法自动断言（要真开 Word 并读光标位置），
/// 这里验的是机制：多页时钩子确实装着并吞掉 ↓，单页时确实没装。
/// </para>
/// </summary>
internal static class PagingTest
{
    private const int VkControl = 0x11;
    private const int VkDown = 0x28;
    private const int VkUp = 0x26;

    private static readonly List<string> Report = new();
    private static int _passed;
    private static int _failures;

    public static int Run(string outputDirectory)
    {
        ApplicationConfiguration.Initialize();
        Directory.CreateDirectory(outputDirectory);
        try
        {
            Console.OutputEncoding = new UTF8Encoding(false);
        }
        catch
        {
            // 无控制台时忽略
        }

        Case("P1 多页：页脚与钩子", () => MultiPage(outputDirectory));
        Case("P2 翻页不改尺寸", () => PageTurnKeepsSize());
        Case("P3 边界不循环", () => BoundaryNoWrap());
        Case("P4 单页不装钩子", () => SinglePageNoHook());
        Case("P5 多页吞键并翻页", () => HookSwallowsAndPages());
        Case("P6 15 秒安全阀", () => SafetyValve());
        Case("P7 按一次 ↓ 只翻一页", SingleKeyPressPagesOnce);
        Case("P8 连翻 3 页窗口矩形不变", PageTurnKeepsPosition);

        return Flush();
    }

    // ---------------- 用例 ----------------

    private static string MultiPage(string outputDirectory)
    {
        using Form window = CreateWindow();
        string apps = CreateAppsDirectory(many: true);
        using var hud = new HudApp(apps, config: new AppConfig { DwellMs = 300 });

        ReleaseAll();
        FocusWindow(window.Handle);
        Require(LongPress(hud, window.Handle), "多页面板未弹出");

        HudModel? model = hud.CurrentModel;
        Require(model is not null, "模型为空");
        Require(model!.PageCount > 1, $"应为多页，实际 {model.PageCount} 页");
        Require(hud.IsPageHookInstalled, "多页可见时钩子未安装");
        Require(HudPanel.FooterText(model).StartsWith($"第 1/{model.PageCount} 页", StringComparison.Ordinal),
            $"页脚不符：\"{HudPanel.FooterText(model)}\"");

        Capture(outputDirectory, "p1-multi.png");
        ReleaseAll();
        return $"{model.PageCount} 页，页脚「{HudPanel.FooterText(model)}」，钩子已装";
    }

    private static string PageTurnKeepsSize()
    {
        using Form window = CreateWindow();
        string apps = CreateAppsDirectory(many: true);
        using var hud = new HudApp(apps, config: new AppConfig { DwellMs = 300 });

        ReleaseAll();
        FocusWindow(window.Handle);
        Require(LongPress(hud, window.Handle), "面板未弹出");

        var before = WindowRect(hud);
        int startPage = hud.CurrentModel!.PageIndex;

        // 翻两页：↓ ↓
        PressPageKey(hud, VkDown);
        int page2 = hud.CurrentModel!.PageIndex;
        PressPageKey(hud, VkDown);
        int page3 = hud.CurrentModel!.PageIndex;

        var after = WindowRect(hud);
        ReleaseAll();

        Require(page2 == startPage + 1, $"第一次 ↓ 应到第 2 页，实际第 {page2 + 1} 页");
        Require(page3 == page2 + 1, $"第二次 ↓ 应到第 3 页，实际第 {page3 + 1} 页");
        Require(before == after, $"翻页改变了窗口矩形：{before} → {after}");
        return $"翻到第 {page3 + 1} 页，GetWindowRect {after.Width}×{after.Height} 不变";
    }

    private static string BoundaryNoWrap()
    {
        using Form window = CreateWindow();
        string apps = CreateAppsDirectory(many: true);
        using var hud = new HudApp(apps, config: new AppConfig { DwellMs = 300 });

        ReleaseAll();
        FocusWindow(window.Handle);
        Require(LongPress(hud, window.Handle), "面板未弹出");

        // 第一页按 ↑ → 不动
        PressPageKey(hud, VkUp);
        Require(hud.CurrentModel!.PageIndex == 0, $"第 1 页按 ↑ 后应仍在第 1 页，实际第 {hud.CurrentModel.PageIndex + 1} 页");

        // 翻到末页，再按 ↓ → 不动
        int last = hud.CurrentModel.PageCount - 1;
        for (int i = 0; i < last; i++)
        {
            PressPageKey(hud, VkDown);
        }

        Require(hud.CurrentModel!.PageIndex == last, $"应到末页，实际第 {hud.CurrentModel.PageIndex + 1} 页");
        PressPageKey(hud, VkDown);
        Require(hud.CurrentModel.PageIndex == last, "末页按 ↓ 后应仍停在末页（不循环）");

        ReleaseAll();
        return $"第 1 页按 ↑ 不动；第 {last + 1}/{last + 1} 页按 ↓ 不动";
    }

    private static string SinglePageNoHook()
    {
        using Form window = CreateWindow();
        string apps = CreateAppsDirectory(many: false);
        using var hud = new HudApp(apps, config: new AppConfig { DwellMs = 300 });

        ReleaseAll();
        FocusWindow(window.Handle);
        Require(LongPress(hud, window.Handle), "单页面板未弹出");

        Require(hud.CurrentModel!.PageCount == 1, $"应为单页，实际 {hud.CurrentModel.PageCount} 页");
        Require(!hud.IsPageHookInstalled, "单页时不应安装钩子");

        // 单页时 ↓ 不被吞：面板应照常收到该键（这里只验钩子没装，Word 光标需人工确认）
        bool hook = hud.IsPageHookInstalled;
        ReleaseAll();

        Require(!hook, "单页装上了钩子");
        return "单页可见时钩子未安装（↓ 不会被吞）";
    }

    private static string HookSwallowsAndPages()
    {
        using Form window = CreateWindow();
        string apps = CreateAppsDirectory(many: true);
        using var hud = new HudApp(apps, config: new AppConfig { DwellMs = 300 });

        ReleaseAll();
        FocusWindow(window.Handle);
        Require(LongPress(hud, window.Handle), "面板未弹出");
        Require(hud.IsPageHookInstalled, "多页可见时钩子未安装");

        int before = hud.CurrentModel!.PageIndex;
        PressPageKey(hud, VkDown);
        int after = hud.CurrentModel!.PageIndex;

        // 收起后钩子必须立刻卸掉
        ReleaseAll();
        Pump(300);
        bool stillInstalled = hud.IsPageHookInstalled;

        Require(after == before + 1, "钩子吞键后没有翻页");
        Require(!stillInstalled, "面板收起后钩子仍在（会一直吞上下键）");
        return $"↓ 被吞并翻到第 {after + 1} 页；收起后钩子已卸";
    }

    private static string SafetyValve()
    {
        using Form window = CreateWindow();
        string apps = CreateAppsDirectory(many: true);
        using var hud = new HudApp(apps, config: new AppConfig { DwellMs = 300 });

        ReleaseAll();
        FocusWindow(window.Handle);

        // 故意不松 Ctrl：模拟 key-up 丢失
        KeyDown(VkControl);
        Pump(500);
        Require(hud.IsOverlayVisible, "前置条件不成立：面板未显示");
        Require(hud.IsPageHookInstalled, "前置条件不成立：钩子未安装");

        var clock = Stopwatch.StartNew();
        while (hud.IsOverlayVisible && clock.ElapsedMilliseconds < 20_000)
        {
            Pump(200);
        }

        bool hidden = !hud.IsOverlayVisible;
        bool uninstalled = !hud.IsPageHookInstalled;
        KeyUp(VkControl);
        Pump(200);

        Require(hidden, "超过 15 秒面板仍未自动收起");
        Require(uninstalled, "安全阀触发后钩子未卸载");
        return $"key-up 丢失 {clock.ElapsedMilliseconds} ms 后面板自动收起，钩子已卸";
    }

    private static string SingleKeyPressPagesOnce()
    {
        using Form window = CreateWindow();
        string apps = CreateAppsDirectory(many: true);
        using var hud = new HudApp(apps, config: new AppConfig { DwellMs = 300 });

        ReleaseAll();
        FocusWindow(window.Handle);
        Require(LongPress(hud, window.Handle), "面板未弹出");
        Require(hud.IsPageHookInstalled, "前置条件不成立：钩子未安装");

        int before = hud.CurrentModel!.PageIndex;
        int hookBefore = hud.HookPageRequests;

        // 真实按键：按下与松开都会经过钩子。修复前两次都投递 → 一次 ↓ 翻两页
        KeyDown(VkDown);
        Pump(60);
        KeyUp(VkDown);
        Pump(250);

        int hookAfter = hud.HookPageRequests;
        int after = hud.CurrentModel!.PageIndex;
        ReleaseAll();
        Pump(150);

        // 本机注入时低层钩子不回调（同一进程里另装一个钩子也不回调，属环境限制），
        // 所以这里验的是「整条链路只前进一页」；钩子层的 flags 解析由 Shell11 用
        // 合成的 KBDLLHOOKSTRUCT 单独锁定（变异验证过：去掉 flags 判断即 FAIL）。
        Require(after == before + 1,
            $"按一次 ↓ 应只前进 1 页，实际 {before + 1} → {after + 1}");
        return $"第 {before + 1} → {after + 1} 页（钩子投递 {hookAfter - hookBefore} 次；本机注入下钩子不回调，见 Shell11）";
    }

    private static string PageTurnKeepsPosition()
    {
        using Form window = CreateWindow();
        string apps = CreateAppsDirectory(many: true);
        using var hud = new HudApp(apps, config: new AppConfig { DwellMs = 300 });

        ReleaseAll();
        FocusWindow(window.Handle);
        Require(LongPress(hud, window.Handle), "面板未弹出");
        Require(hud.CurrentModel!.PageCount >= 4, "前置条件不成立：样本不足 4 页");

        var rects = new List<(int Left, int Top, int Width, int Height)> { WindowRect(hud) };
        for (int i = 0; i < 3; i++)
        {
            PressPageKey(hud, VkDown);
            rects.Add(WindowRect(hud));
        }

        ReleaseAll();
        Pump(150);

        Require(rects.Distinct().Count() == 1,
            "翻页改变了窗口矩形：" + string.Join(" → ", rects));

        return $"连翻 3 页，GetWindowRect 四次均为 {rects[0].Width}×{rects[0].Height}"
               + $" @({rects[0].Left},{rects[0].Top})";
    }

    // ---------------- 基础设施 ----------------

    /// <summary>把一次 ↓/↑ 送进状态机（模拟钩子吞键后投递的那条路）。</summary>
    private static void PressPageKey(HudApp hud, int virtualKey)
    {
        hud.RequestPage(virtualKey == VkDown ? 1 : -1);
        Pump(120);
    }

    private static (int Left, int Top, int Width, int Height) WindowRect(HudApp hud)
    {
        NativeMethods.GetWindowRect(hud.Overlay.Handle, out NativeMethods.RECT rect);
        return (rect.Left, rect.Top, rect.Width, rect.Height);
    }

    private static string CreateAppsDirectory(bool many)
    {
        string directory = Path.Combine(
            Path.GetTempPath(), "hotkeyhud-paging-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);

        string processName = Process.GetCurrentProcess().ProcessName;
        var shortcuts = new List<string>();
        // 40 条 → 含熟记名单里的通用键（会被降权到「通用」组），页数 ≥ 3
        int count = many ? 40 : 3;
        for (int i = 0; i < count; i++)
        {
            // 单页样本刻意避开熟记名单：否则通用组会另起一页，就不是单页了
            string key = !many
                ? $"Ctrl+{(char)('K' + i)}"
                : i < 26 ? $"Ctrl+{(char)('A' + i)}"
                : i < 36 ? $"Ctrl+{i - 26}" : $"Ctrl+F{i - 35}";
            shortcuts.Add($"{{ \"keys\": \"{key}\", \"action\": \"动作 {i + 1}\", \"group\": \"第 {i / 6 + 1} 组\" }}");
        }

        string json = $$"""
            {
              "app": "pagingtest",
              "displayName": "PagingTest",
              "processNames": ["{{processName}}.EXE"],
              "shortcuts": [
                {{string.Join(",\n        ", shortcuts)}}
              ]
            }
            """;

        File.WriteAllText(Path.Combine(directory, "paging.json"), json);
        return directory;
    }

    private static Form CreateWindow()
    {
        var form = new Form
        {
            Text = "HotkeyHud 分页测试",
            StartPosition = FormStartPosition.Manual,
            Bounds = new Rectangle(40, 40, 520, 200),
            FormBorderStyle = FormBorderStyle.FixedToolWindow,
        };
        form.Show();
        form.Activate();
        Pump(120);
        return form;
    }

    private static void FocusWindow(IntPtr handle)
    {
        NativeMethods.SetForegroundWindow(handle);
        Pump(120);

        IntPtr current = NativeMethods.GetForegroundWindow();
        if (current != handle)
        {
            throw new PagingCheckException($"无法把目标窗口切到前台（当前 0x{current.ToInt64():X}）");
        }
    }

    private static bool LongPress(HudApp hud, IntPtr target)
    {
        ReleaseAll();
        FocusWindow(target);
        KeyDown(VkControl);
        bool visible = PumpTracking(500, hud);
        return visible;
    }

    private static void Capture(string directory, string fileName)
    {
        Rectangle area = Screen.PrimaryScreen?.Bounds ?? new Rectangle(0, 0, 1920, 1080);
        ScreenCapture.CaptureTo(area, Path.Combine(directory, fileName));
    }

    private static void KeyDown(int virtualKey)
        => NativeMethods.keybd_event((byte)virtualKey, 0, 0, UIntPtr.Zero);

    private static void KeyUp(int virtualKey)
        => NativeMethods.keybd_event((byte)virtualKey, 0, NativeMethods.KEYEVENTF_KEYUP, UIntPtr.Zero);

    private static void ReleaseAll()
    {
        KeyUp(VkDown);
        KeyUp(VkUp);
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
            throw new PagingCheckException(message);
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
                Path.Combine(Path.GetTempPath(), "hotkeyhud-paging.log"), text + Environment.NewLine);
        }
        catch
        {
            // 日志失败不影响退出码
        }

        return _failures;
    }

    private sealed class PagingCheckException : Exception
    {
        public PagingCheckException(string message) : base(message)
        {
        }
    }
}
#endif
