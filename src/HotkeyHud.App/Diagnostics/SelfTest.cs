using System.Diagnostics;
using System.Runtime.InteropServices;
using HotkeyHud.App.Platform;

namespace HotkeyHud.App.Diagnostics;

/// <summary>
/// S1 验收自检：证明「不干扰」的四条——输入通路可用且不阻塞、点击穿透、不抢焦点、
/// 不进 Alt+Tab / 任务栏。输出同时写标准输出与 %TEMP%\hotkeyhud-selftest.log。
/// </summary>
internal static class SelfTest
{
    private const int VkNoOp = 0x87; // VK_F24：无任何应用会绑定的空键

    private static readonly List<string> Report = new();
    private static int _failures;

    public static int Run()
    {
        ApplicationConfiguration.Initialize();
        try
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8;
        }
        catch
        {
            // 无控制台时忽略
        }

        var captured = new List<InputEvent>();
        using var hud = new HudApp();
        hud.Input.Input += captured.Add;
        hud.Input.CollectStats = true;

        CheckInputPath(hud, captured);
        CheckOverlay(hud);
        CheckMemory();

        return Flush();
    }

    private static void CheckInputPath(HudApp hud, List<InputEvent> captured)
    {
        // 预热：首次 WM_INPUT 会触发 JIT 与结构体封送初始化，不能算进稳态耗时
        InjectNoOpKeys(10);
        Pump(150);
        hud.Input.ResetCallbackStats();
        captured.Clear();

        // F24 无任何副作用，不会触发应用快捷键；300 次按下/松开 = 600 个 Raw Input 事件
        InjectNoOpKeys(300);
        Pump(300);

        bool sawDown = captured.Any(e => e.Kind == InputKind.Key && e.Code == VkNoOp && e.IsDown);
        bool sawUp = captured.Any(e => e.Kind == InputKind.Key && e.Code == VkNoOp && !e.IsDown);

        Record(sawDown && sawUp, "S1-A1 Raw Input 通路",
            $"F24 按下={sawDown} 松开={sawUp}，共收到 {captured.Count} 个事件");

        // 双判据：p50 抓稳态开销（阈值 0.5ms），p99 抓偶发阻塞路径（阈值 5ms）。
        // 单看 p99 会因机器繁忙误报，单看 p50 又抓不到「某些事件特别慢」的缺陷。
        // Raw Input 是异步投递，回调耗时不会阻塞被输入窗口的按键——这一点由 --typing-test 实测证明。
        // 必须同时断言样本数：没有样本时各分位返回 0，会变成永远通过的假断言。
        int samples = captured.Count;
        double p50 = hud.Input.P50CallbackMs;
        double p99 = hud.Input.P99CallbackMs;
        bool callbackFast = samples > 0 && p50 < 0.5 && p99 < 5.0;
        Record(callbackFast, "S1-A2 输入回调不阻塞",
            $"p50 = {p50:F3} ms（阈值 0.500），p99 = {p99:F3} ms（阈值 5.000），"
            + $"最大 {hud.Input.MaxCallbackMs:F3} ms，样本 {samples} 个");
    }

    private static void InjectNoOpKeys(int count)
    {
        for (int i = 0; i < count; i++)
        {
            NativeMethods.keybd_event(VkNoOp, 0, 0, UIntPtr.Zero);
            NativeMethods.keybd_event(VkNoOp, 0, NativeMethods.KEYEVENTF_KEYUP, UIntPtr.Zero);
            Pump(2);
        }
    }

    private static void CheckOverlay(HudApp hud)
    {
        IntPtr foregroundBefore = NativeMethods.GetForegroundWindow();

        hud.ShowOverlay();
        Pump(200);

        IntPtr hwnd = hud.Overlay.Handle;
        bool visible = NativeMethods.IsWindowVisible(hwnd);
        Rectangle panel = HudApp.PanelBounds();
        var center = new NativeMethods.POINT(
            panel.X + panel.Width / 2, panel.Y + panel.Height / 2);
        IntPtr hit = NativeMethods.WindowFromPoint(center);

        // 必须先确认窗口真的显示了，否则「点不中它」毫无意义
        Record(visible && hit != hwnd, "S1-B 鼠标穿透",
            $"可见={visible}，WindowFromPoint(屏幕中心) = 0x{hit.ToInt64():X}，覆盖窗 = 0x{hwnd.ToInt64():X}");

        IntPtr foregroundAfter = NativeMethods.GetForegroundWindow();
        Record(foregroundBefore == foregroundAfter, "S1-C 不抢焦点",
            $"显示前 = 0x{foregroundBefore.ToInt64():X}，显示后 = 0x{foregroundAfter.ToInt64():X}");

        long exStyle = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE).ToInt64();
        bool toolWindow = (exStyle & NativeMethods.WS_EX_TOOLWINDOW) != 0;
        bool appWindow = (exStyle & NativeMethods.WS_EX_APPWINDOW) != 0;
        // 说明：Alt+Tab 列表没有公开的查询 API，这里验证的是控制它的窗口样式机制。
        // 真人确认「Alt+Tab 里看不到面板」仍是交付前的手工步骤。
        Record(toolWindow && !appWindow, "S1-D 不进 Alt+Tab / 任务栏（样式机制）",
            $"WS_EX_TOOLWINDOW={toolWindow} WS_EX_APPWINDOW={appWindow}，可见={visible}");

        NativeMethods.GetWindowRect(hwnd, out NativeMethods.RECT rect);
        bool geometryOk = rect.Left == panel.X && rect.Top == panel.Y
                          && rect.Width == panel.Width && rect.Height == panel.Height;
        Record(geometryOk, "S1-E 覆盖窗几何",
            $"期望 ({panel.X},{panel.Y}) {panel.Width}×{panel.Height}，"
            + $"实际 ({rect.Left},{rect.Top}) {rect.Width}×{rect.Height}");

        hud.HideOverlay();
        Pump(120);
        Record(!NativeMethods.IsWindowVisible(hwnd), "S1-F 可收起",
            $"SW_HIDE 后 IsWindowVisible={NativeMethods.IsWindowVisible(hwnd)}");
    }

    private static void CheckMemory()
    {
        using var process = Process.GetCurrentProcess();
        process.Refresh();
        double commitMb = process.PrivateMemorySize64 / 1024.0 / 1024.0;
        double workingMb = process.WorkingSet64 / 1024.0 / 1024.0;

        // §八 的「空载私有工作集」在任务管理器里对应「提交大小」这一列。
        // WorkingSet64 含 CLR / WinForms 程序集映射的共享页，不是私有口径，只作参考。
        Record(commitMb <= 45.0, "性能-空载提交大小",
            $"提交大小 {commitMb:F1} MB（§八 目标 ≤ 45 MB）；总工作集 {workingMb:F1} MB 含共享框架页");
    }

    private static void Record(bool passed, string name, string detail)
    {
        if (!passed)
        {
            _failures++;
        }

        Report.Add($"[{(passed ? "PASS" : "FAIL")}] {name} — {detail}");
    }

    private static int Flush()
    {
        int total = Report.Count;
        Report.Add($"--- 通过 {total - _failures}，失败 {_failures} ---");

        string text = string.Join(Environment.NewLine, Report);
        TryWriteConsole(text);
        WriteLogFile(text);
        return _failures;
    }

    private static void TryWriteConsole(string text)
    {
        try
        {
            using var stdout = Console.OpenStandardOutput();
            using var writer = new StreamWriter(stdout) { AutoFlush = true };
            writer.WriteLine(text);
        }
        catch
        {
            // WinExe 可能没有可写标准输出；文件日志是兜底
        }
    }

    private static void WriteLogFile(string text)
    {
        try
        {
            string path = Path.Combine(Path.GetTempPath(), "hotkeyhud-selftest.log");
            File.WriteAllText(path, text + Environment.NewLine);
        }
        catch
        {
            // 自检日志写失败不影响退出码
        }
    }

    private static void Pump(int milliseconds)
    {
        long deadline = Environment.TickCount64 + milliseconds;
        while (Environment.TickCount64 < deadline)
        {
            Application.DoEvents();
            Thread.Sleep(5);
        }
    }
}
