using System.Diagnostics;
using System.Text;
using HotkeyHud.App.Platform;

namespace HotkeyHud.App.Diagnostics;

/// <summary>
/// S1 验收 ①：覆盖窗显示期间，向另一个窗口连续输入，验证「不延迟、不丢字、不抢焦点」。
/// 用进程内 TextBox 作为被输入方，注入按键走与物理键盘相同的系统输入队列。
/// </summary>
internal static class TypingTest
{
    private const string Pattern = "abc123";

    public static int Run(int seconds, string? capturePath)
    {
        ApplicationConfiguration.Initialize();
        try
        {
            Console.OutputEncoding = Encoding.UTF8;
        }
        catch
        {
            // 无控制台时忽略
        }

        var textBox = new TextBox
        {
            Multiline = true,
            Dock = DockStyle.Fill,
            Font = new Font("Consolas", 11f),
            ScrollBars = ScrollBars.Vertical,
            WordWrap = false,
        };

        using var target = new Form
        {
            Text = "HotkeyHud 输入目标窗口",
            StartPosition = FormStartPosition.Manual,
            Location = new Point(40, 40),
            Size = new Size(520, 260),
            FormBorderStyle = FormBorderStyle.FixedToolWindow,
            ShowInTaskbar = true,
        };
        target.Controls.Add(textBox);

        using var hud = new HudApp();

        var report = new List<string>();
        var clock = Stopwatch.StartNew();
        var expected = new StringBuilder();

        target.Show();
        target.Activate();
        textBox.Focus();
        Pump(200);

        IntPtr targetHandle = target.Handle;
        IntPtr foregroundBeforeOverlay = NativeMethods.GetForegroundWindow();

        hud.ShowOverlay();
        Pump(200);

        IntPtr foregroundAfterOverlay = NativeMethods.GetForegroundWindow();
        bool overlayKeptFocus = foregroundAfterOverlay == foregroundBeforeOverlay;

        // 逐个字符注入，直到时间用尽。Unicode 注入绕过键盘布局/输入法，
        // 但它不经过 Raw Input 通路，因此本测试不用于评估回调耗时（那由 --selftest 覆盖）。
        var lastCharClock = Stopwatch.StartNew();
        int index = 0;
        int injectionFailures = 0;
        bool focusLost = false;
        while (clock.Elapsed.TotalSeconds < seconds)
        {
            // 前台窗口一旦被切走必须立刻中止，否则剩余成千上万个字符会打进用户的文档
            if (NativeMethods.GetForegroundWindow() != targetHandle)
            {
                focusLost = true;
                break;
            }

            char c = Pattern[index++ % Pattern.Length];
            if (NativeMethods.SendUnicodeChar(c))
            {
                expected.Append(c);
            }
            else
            {
                injectionFailures++;
            }

            // 5ms 间隔：过快注入会超出系统输入队列，测出来的是注入器而不是 HUD
            while (lastCharClock.Elapsed.TotalMilliseconds < 5)
            {
                Application.DoEvents();
            }

            lastCharClock.Restart();
        }

        Pump(400);

        string actual = textBox.Text;
        string want = expected.ToString();
        int mismatch = FirstMismatch(actual, want);

        if (capturePath is not null)
        {
            ScreenCapture.CaptureTo(new Rectangle(20, 20, 600, 340), capturePath);
        }

        hud.HideOverlay();
        Pump(100);

        double elapsed = clock.Elapsed.TotalSeconds;
        bool textOk = mismatch < 0 && !focusLost && injectionFailures == 0;
        report.Add($"[{(textOk ? "PASS" : "FAIL")}] S1-G 连续输入不丢字 — "
                   + $"注入 {want.Length} 字符 / {elapsed:F1}s（注入失败 {injectionFailures} 次），"
                   + $"实际收到 {actual.Length} 字符"
                   + (mismatch < 0 ? "，逐字一致" : $"，第 {mismatch} 个字符起不一致")
                   + (focusLost ? "；前台窗口被切走，测试已中止" : string.Empty));
        if (mismatch >= 0)
        {
            report.Add($"      期望前 40 字：{Head(want)}");
            report.Add($"      实际前 40 字：{Head(actual)}");
        }

        report.Add($"[{(overlayKeptFocus ? "PASS" : "FAIL")}] S1-H 覆盖窗显示期间焦点不变 — "
                   + $"目标窗 0x{targetHandle.ToInt64():X}，覆盖窗前 0x{foregroundBeforeOverlay.ToInt64():X}，"
                   + $"覆盖窗后 0x{foregroundAfterOverlay.ToInt64():X}");

        int failures = report.Count(line => line.StartsWith("[FAIL]", StringComparison.Ordinal));
        report.Add($"--- 通过 {report.Count - failures}，失败 {failures} ---");

        string text = string.Join(Environment.NewLine, report);
        Console.WriteLine(text);
        try
        {
            File.WriteAllText(Path.Combine(Path.GetTempPath(), "hotkeyhud-typingtest.log"), text + Environment.NewLine);
        }
        catch
        {
            // 日志失败不影响退出码
        }

        return failures;
    }

    private static string Head(string value, int length = 40)
        => value.Length <= length ? value : value[..length];

    private static int FirstMismatch(string actual, string expected)    {
        int limit = Math.Min(actual.Length, expected.Length);
        for (int i = 0; i < limit; i++)
        {
            if (actual[i] != expected[i])
            {
                return i;
            }
        }

        return actual.Length == expected.Length ? -1 : limit;
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
