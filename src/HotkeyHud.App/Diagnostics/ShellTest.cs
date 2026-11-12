#if DEBUG
using System.Diagnostics;
using System.Text;
using HotkeyHud.App.Platform;
using HotkeyHud.App.Shell;
using HotkeyHud.Core;

namespace HotkeyHud.App.Diagnostics;

/// <summary>
/// S5 外壳验收：托盘菜单、暂停、抑制并集、配置真的生效、配置损坏、自启、单实例。
/// <para>
/// 用例 5、6 是重点——配置最容易做成「读了但没接上」，所以这里一律用行为断言，
/// 不看文件是否被读取。
/// </para>
/// </summary>
internal static class ShellTest
{
    private const int VkControl = 0x11;
    private const int VkC = 0x43;

    private static readonly List<string> Report = new();
    private static int _passed;
    private static int _failures;

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

        Case("Shell01 托盘菜单 6 项可用", TrayMenuComplete);
        Case("Shell02 暂停生效", PauseWorks);
        Case("Shell03 暂停与游戏抑制互不覆盖", PauseSurvivesGameSuppression);
        Case("Shell04 dwellMs 真的生效", DwellMsTakesEffect);
        Case("Shell05 knownShortcuts=[] 真的生效", KnownShortcutsTakesEffect);
        Case("Shell06 配置损坏不崩且留证", CorruptConfigHandled);
        Case("Shell07 开机自启可逆", AutoStartReversible);
        Case("Shell08 单实例跨进程生效", SingleInstanceBlocksSecond);

        return Flush();
    }

    // ---------------- 用例 ----------------

    private static string TrayMenuComplete()
    {
        using var tray = new TrayIcon(() => { }, () => { }, () => { }, _ => { }, () => { });

        tray.Update(paused: false, suppressed: false, reason: "", foreground: "chrome.exe", autoStart: false);
        string normal = tray.StatusText;
        IReadOnlyList<string> actions = tray.ActionItems;

        tray.Update(paused: true, suppressed: true, reason: "手动暂停", foreground: "chrome.exe", autoStart: true);
        string paused = tray.StatusText;
        Require(actions.Count == 5,
            $"可点击菜单项应为 5 个（暂停/重载/打开配置/开机自启/退出），实际 {actions.Count}："
            + string.Join("、", actions));
        Require(actions.Contains("暂停") && actions.Contains("重载数据")
                && actions.Contains("打开配置目录") && actions.Contains("开机自启")
                && actions.Contains("退出"),
            $"菜单项不全：{string.Join("、", actions)}");

        Require(normal.Contains("正常") && normal.Contains("chrome.exe"),
            $"正常态状态行不对：\"{normal}\"");
        Require(paused.Contains("已抑制") && paused.Contains("手动暂停"),
            $"抑制态状态行不对：\"{paused}\"");
        Require(tray.PauseItemText == "恢复", $"暂停后菜单项应显示「恢复」，实际 \"{tray.PauseItemText}\"");

        return $"5 个可点击项 + 状态项；正常=\"{normal}\"，暂停=\"{paused}\"";
    }

    private static string PauseWorks()
    {
        using Form normal = CreateWindow("HotkeyHud 普通窗口", new Rectangle(40, 40, 520, 200));
        using var hud = new HudApp();
        ReleaseAll();
        FocusWindow(normal.Handle);

        hud.Suppressed = true;
        bool whilePaused = LongPressShows(hud, normal.Handle);

        hud.Suppressed = false;
        bool afterResume = LongPressShows(hud, normal.Handle);

        Require(!whilePaused, "暂停期间仍然弹出了面板");
        Require(afterResume, "恢复后没有弹出面板");
        return "暂停期间不弹，恢复后正常弹";
    }

    private static string PauseSurvivesGameSuppression()
    {
        using Form normal = CreateWindow("HotkeyHud 普通窗口", new Rectangle(40, 40, 520, 200));
        using Form fullscreen = CreateFullscreenWindow();
        using var hud = new HudApp();

        ReleaseAll();
        hud.Suppressed = true; // 手动暂停
        FocusWindow(fullscreen.Handle);
        Pump(250);
        Require(hud.GameSuppressed, "前置条件不成立：全屏下未抑制");

        fullscreen.Close();
        Pump(200);
        FocusWindow(normal.Handle);

        var wait = Stopwatch.StartNew();
        while (hud.GameSuppressed && wait.ElapsedMilliseconds < 4000)
        {
            Pump(250);
        }

        Require(!hud.GameSuppressed, $"退出全屏后游戏抑制未解除（QUNS={hud.NotificationState}）");
        Require(hud.Suppressed, "游戏抑制解除时把手动暂停一起覆盖掉了");

        bool shown = LongPressShows(hud, normal.Handle);
        Require(!shown, "手动暂停仍然生效的情况下弹出了面板");

        hud.Suppressed = false;
        Require(LongPressShows(hud, normal.Handle), "取消暂停后仍未弹出");
        return "暂停 → 全屏 → 退出全屏后仍处于暂停，取消暂停后恢复";
    }

    private static string DwellMsTakesEffect()
    {
        using Form normal = CreateWindow("HotkeyHud 普通窗口", new Rectangle(40, 40, 520, 200));
        var config = new AppConfig { DwellMs = 600 };
        using var hud = new HudApp(config: config);

        ReleaseAll();
        FocusWindow(normal.Handle);
        hud.Suppressed = false;

        bool at400 = HoldFor(hud, normal.Handle, 400);
        bool at650 = HoldFor(hud, normal.Handle, 650);

        Require(!at400, "dwellMs=600 时按住 400ms 就弹了，配置没生效");
        Require(at650, "dwellMs=600 时按住 650ms 仍未弹出");
        return "dwellMs=600：400ms 不弹、650ms 弹";
    }

    private static string KnownShortcutsTakesEffect()
    {
        using Form normal = CreateWindow("HotkeyHud 普通窗口", new Rectangle(40, 40, 520, 200));
        string appsDirectory = CreateAppsDirectory();
        var config = new AppConfig { KnownShortcuts = Array.Empty<string>() };
        using var hud = new HudApp(appsDirectory, config: config);

        ReleaseAll();
        FocusWindow(normal.Handle);
        hud.Suppressed = false;

        // 面板里的条目：Ctrl+C 默认被过滤掉，关闭过滤后必须出现
        KeyDown(VkControl);
        Require(PumpTracking(500, hud), "前置条件不成立：面板未弹出");
        Require(hud.CurrentLabel.Contains("Ctrl"), $"label 异常：\"{hud.CurrentLabel}\"");

        KeyDown(VkC);
        Pump(60);
        KeyUp(VkC);
        bool stillVisible = hud.IsOverlayVisible;
        Combo? highlighted = hud.HighlightedCombo;
        KeyUp(VkControl);
        Pump(150);

        Require(stillVisible, "knownShortcuts=[] 后按 Ctrl+C 仍然立即收起（说明过滤没关掉）");
        Require(highlighted == new Combo(Modifier.Ctrl, VkC),
            $"按 Ctrl+C 后未高亮该条：{highlighted?.ToString() ?? "null"}");

        return "knownShortcuts=[] 后 Ctrl+C 出现在列表且会高亮";
    }

    private static string CorruptConfigHandled()
    {
        string directory = Path.Combine(
            Path.GetTempPath(), "hotkeyhud-config-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);

        try
        {
            var store = new ConfigStore(directory);
            File.WriteAllText(store.FilePath, "{ \"dwellMs\": 600, \"knownShortcuts\": [ this is not json");

            AppConfig config = store.Load();

            Require(store.LastLoadWasCorrupt, "损坏的配置没有被识别");
            Require(config.DwellMs == AppConfig.DefaultDwellMs,
                $"损坏后应回到默认 dwellMs，实际 {config.DwellMs}");
            Require(File.Exists(store.BrokenPath), "没有生成 config.broken.json 留证");
            Require(!File.Exists(store.FilePath) || IsValidConfig(store.FilePath),
                "损坏的原文件既没被改名，也没被重写成合法配置");

            string broken = File.ReadAllText(store.BrokenPath);
            Require(broken.Contains("not json", StringComparison.Ordinal),
                "config.broken.json 里不是原始内容");

            return $"回落到默认值，原文件已改名为 {Path.GetFileName(store.BrokenPath)}";
        }
        finally
        {
            TryDelete(directory);
        }
    }

    private static string AutoStartReversible()
    {
        bool original = AutoStart.IsEnabled();
        string? originalCommand = AutoStart.CurrentCommand();

        try
        {
            AutoStart.Set(true);
            string? written = AutoStart.CurrentCommand();
            string expected = AutoStart.ExpectedCommand();

            Require(AutoStart.IsEnabled(), "写入后注册表仍显示未启用");
            Require(written == expected, $"写入值不符：\"{written}\"，期望 \"{expected}\"");
            Require(written is not null && written.StartsWith('"') && written.EndsWith('"'),
                $"路径未加引号：\"{written}\"");
            Require(written!.Contains("HotkeyHud", StringComparison.OrdinalIgnoreCase),
                $"路径里没有程序名：\"{written}\"");

            AutoStart.Set(false);
            Require(!AutoStart.IsEnabled(), "删除后注册表仍显示启用");
            Require(AutoStart.CurrentCommand() is null, "删除后仍能读到值");

            return $"写入 \"{written}\" → 删除后为空";
        }
        finally
        {
            // 还原用户原本的状态，别把机器搞脏
            try
            {
                if (original && originalCommand is not null)
                {
                    AutoStart.Set(true);
                }
                else
                {
                    AutoStart.Set(false);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[HotkeyHud] 还原自启状态失败：{ex}");
            }
        }
    }

    private static string SingleInstanceBlocksSecond()
    {
        using var first = new SingleInstance();
        Require(first.TryAcquire(), "第一次获取单实例锁失败");

        string? exe = Environment.ProcessPath;
        Require(!string.IsNullOrEmpty(exe), "Environment.ProcessPath 为空，无法启动子进程");

        var info = new ProcessStartInfo
        {
            FileName = exe,
            Arguments = "--probe-single-instance",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        using Process second = Process.Start(info)
                               ?? throw new InvalidOperationException("子进程未启动");
        if (!second.WaitForExit(10_000))
        {
            second.Kill(entireProcessTree: true);
            throw new InvalidOperationException("第二个实例没有在 10 秒内退出");
        }

        Require(second.ExitCode == 3,
            $"第二个实例退出码 {second.ExitCode}，期望 3（表示已被占用）");

        first.Dispose();
        using var afterRelease = new SingleInstance();
        Require(afterRelease.TryAcquire(), "第一个实例释放后仍拿不到锁");

        return "子进程识别到已在运行并以退出码 3 退出";
    }

    // ---------------- 基础设施 ----------------

    private static bool IsValidConfig(string path)
    {
        try
        {
            var store = new ConfigStore(Path.GetDirectoryName(path)!);
            _ = store.Load();
            return !store.LastLoadWasCorrupt;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>临时档案目录：给本进程写一份含 Ctrl+C / Ctrl+K 的档案，用于验证过滤开关。</summary>
    private static string CreateAppsDirectory()
    {
        string directory = Path.Combine(
            Path.GetTempPath(), "hotkeyhud-apps-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);

        string processName = Process.GetCurrentProcess().ProcessName;
        string json = $$"""
            {
              "app": "shelltest",
              "displayName": "ShellTestApp",
              "processNames": ["{{processName}}.EXE"],
              "shortcuts": [
                { "keys": "Ctrl+C", "action": "复制（默认被过滤）", "group": "编辑" },
                { "keys": "Ctrl+K", "action": "插入超链接", "group": "编辑" }
              ]
            }
            """;

        File.WriteAllText(Path.Combine(directory, "shelltest.json"), json);
        return directory;
    }

    private static void TryDelete(string directory)
    {
        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (Exception)
        {
            // 临时目录清理失败不影响结论
        }
    }

    /// <summary>按住指定时长，返回面板是否出现过。</summary>
    private static bool HoldFor(HudApp hud, IntPtr target, int milliseconds)
    {
        ReleaseAll();
        FocusWindow(target);
        KeyDown(VkControl);
        bool visible = PumpTracking(milliseconds, hud);
        KeyUp(VkControl);
        Pump(200);
        return visible;
    }

    private static bool LongPressShows(HudApp hud, IntPtr target) => HoldFor(hud, target, 500);

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
            throw new ShellCheckException(
                $"无法把目标窗口切到前台（当前前台 = 0x{current.ToInt64():X}）");
        }
    }

    private static void KeyDown(int virtualKey)
        => NativeMethods.keybd_event((byte)virtualKey, 0, 0, UIntPtr.Zero);

    private static void KeyUp(int virtualKey)
        => NativeMethods.keybd_event((byte)virtualKey, 0, NativeMethods.KEYEVENTF_KEYUP, UIntPtr.Zero);

    private static void ReleaseAll()
    {
        KeyUp(VkC);
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
            throw new ShellCheckException(message);
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
                Path.Combine(Path.GetTempPath(), "hotkeyhud-shell.log"), text + Environment.NewLine);
        }
        catch
        {
            // 日志失败不影响退出码
        }

        return _failures;
    }

    private sealed class ShellCheckException : Exception
    {
        public ShellCheckException(string message) : base(message)
        {
        }
    }
}
#endif
