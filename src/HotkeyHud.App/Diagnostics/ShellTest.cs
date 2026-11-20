#if DEBUG
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using HotkeyHud.App.Platform;
using HotkeyHud.App.Shell;
using HotkeyHud.App.Shell.Adapters;
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
        Case("Shell09 用户档案：覆盖内置 / 降权豁免 / 新增生效", UserProfileMerges);
        Case("Shell10 适配器与模板：VS Code / JetBrains / 模板可被加载", AdaptersAndTemplate);
        Case("Shell11 钩子只对按下翻页（flags 偏移 8）", HookFlagsParsed);

        return Flush();
    }

    // ---------------- 用例 ----------------

    private static string TrayMenuComplete()
    {
        using var tray = new TrayIcon(() => { }, () => { }, () => { }, () => { }, _ => { }, () => { });

        tray.Update(paused: false, suppressed: false, reason: "", foreground: "chrome.exe", autoStart: false);
        string normal = tray.StatusText;
        IReadOnlyList<string> actions = tray.ActionItems;

        tray.Update(paused: true, suppressed: true, reason: "手动暂停", foreground: "chrome.exe", autoStart: true);
        string paused = tray.StatusText;
        Require(actions.Count == 6,
            $"可点击菜单项应为 6 个（暂停/添加应用快捷键/重载/打开配置/开机自启/退出），实际 {actions.Count}："
            + string.Join("、", actions));
        Require(actions.Contains("暂停") && actions.Contains("添加应用快捷键…")
                && actions.Contains("重载数据") && actions.Contains("打开配置目录")
                && actions.Contains("开机自启") && actions.Contains("退出"),
            $"菜单项不全：{string.Join("、", actions)}");

        Require(normal.Contains("正常") && normal.Contains("chrome.exe"),
            $"正常态状态行不对：\"{normal}\"");
        Require(paused.Contains("已抑制") && paused.Contains("手动暂停"),
            $"抑制态状态行不对：\"{paused}\"");
        Require(tray.PauseItemText == "恢复", $"暂停后菜单项应显示「恢复」，实际 \"{tray.PauseItemText}\"");

        return $"6 个可点击项 + 状态项；正常=\"{normal}\"，暂停=\"{paused}\"";
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

        KeyDown(VkControl);
        Require(PumpTracking(500, hud), "前置条件不成立：面板未弹出");
        Require(hud.CurrentLabel.Contains("Ctrl"), $"label 异常：\"{hud.CurrentLabel}\"");

        HudModel model = hud.CurrentModel
            ?? throw new ShellCheckException("拿不到面板模型");

        // §4.3 v1.7：名单为空 → 没有任何条目被降权，也就没有「通用」组
        Require(model.DeferredKnownCount == 0,
            $"knownShortcuts=[] 时不应有降权条目，实际 {model.DeferredKnownCount}");
        Require(!model.Groups.Any(g => g.Title == HudBuilder.GeneralGroupTitle),
            "knownShortcuts=[] 时不应出现「通用」组");
        Require(model.Groups.Any(g => g.Items.Any(i => i.Keys == "C")),
            "Ctrl+C 应出现在应用自己的组里");

        KeyDown(VkC);
        Pump(60);
        KeyUp(VkC);
        bool stillVisible = hud.IsOverlayVisible;
        Combo? highlighted = hud.HighlightedCombo;
        KeyUp(VkControl);
        Pump(150);

        Require(stillVisible, "knownShortcuts=[] 后按 Ctrl+C 仍应立即收起（说明配置没接上）");
        Require(highlighted == new Combo(Modifier.Ctrl, VkC),
            $"按 Ctrl+C 后未高亮该条：{highlighted?.ToString() ?? "null"}");

        return "knownShortcuts=[] 后无「通用」组，Ctrl+C 在应用组内且会高亮";
    }

    private static string UserProfileMerges()
    {
        using Form normal = CreateWindow("HotkeyHud 普通窗口", new Rectangle(40, 40, 520, 200));
        string processName = Process.GetCurrentProcess().ProcessName;

        using var builtin = new TempDir();
        using var user = new TempDir();

        builtin.Write("huduser.json", $$"""
            {
              "app": "huduser",
              "displayName": "HudUser",
              "processNames": ["{{processName}}.EXE"],
              "shortcuts": [
                { "keys": "Ctrl+K", "action": "内置链接", "group": "编辑" },
                { "keys": "Ctrl+F", "action": "内置查找", "group": "编辑" },
                { "keys": "Ctrl+C", "action": "内置复制", "group": "编辑" }
              ]
            }
            """);

        user.Write("huduser.json", $$"""
            {
              "app": "huduser",
              "displayName": "HudUser",
              "processNames": ["{{processName}}.EXE"],
              "shortcuts": [
                { "keys": "Ctrl+F", "action": "我的查找", "group": "编辑" },
                { "keys": "Ctrl+C", "action": "我的复制", "group": "编辑" },
                { "keys": "Ctrl+G", "action": "我的跳转", "group": "编辑" }
              ]
            }
            """);

        using var hud = new HudApp(builtin.Path, userAppsDirectory: user.Path);

        ReleaseAll();
        FocusWindow(normal.Handle);
        hud.Suppressed = false;

        KeyDown(VkControl);
        Require(PumpTracking(500, hud), "前置条件不成立：面板未弹出");

        HudModel model = hud.CurrentModel ?? throw new ShellCheckException("拿不到面板模型");
        KeyUp(VkControl);
        Pump(150);

        List<HudItem> items = model.Groups.SelectMany(g => g.Items).ToList();

        Require(items.Any(i => i.Keys == "F" && i.Action == "我的查找"),
            "同 keys 应被用户档案覆盖（期望「我的查找」）");
        Require(items.Any(i => i.Keys == "G" && i.Action == "我的跳转"), "用户新增条目没出现");
        Require(items.Any(i => i.Keys == "C" && i.Action == "我的复制"),
            "用户写的 Ctrl+C 应保留自己的文案");
        Require(!model.Groups.Any(g => g.Title == HudBuilder.GeneralGroupTitle),
            "用户显式写的 Ctrl+C 不应被降权到「通用」组");

        return $"覆盖=F/我的查找，新增=G/我的跳转，Ctrl+C 留在「{model.Groups[0].Title}」组";
    }

    private static string AdaptersAndTemplate()
    {
        using var appData = new TempDir();
        using var userDir = new TempDir();

        // --- VS Code：用户覆盖层 ---
        string codeUser = Path.Combine(appData.Path, "Code", "User");
        Directory.CreateDirectory(codeUser);
        File.WriteAllText(Path.Combine(codeUser, "keybindings.json"), """
            [
              { "key": "ctrl+alt+f", "command": "editor.action.formatDocument" },
              { "key": "ctrl+k ctrl+s", "command": "workbench.action.files.saveAll" },
              { "key": "ctrl+alt+x", "command": "-editor.action.deleteLines" },
              { "key": "ctrl+shift+escape", "command": "workbench.action.terminal.new" }
            ]
            """);

        bool vscHit = VsCodeAdapter.TryRead(
            @"C:/x/Code.exe", appData.Path, out List<AdapterEntry> vscEntries, out string vscReason);
        Require(vscHit, $"VS Code 适配器应命中：{vscReason}");
        Require(vscEntries.Count == 2, $"应读到 2 条（多键序列与解绑要跳过），实际 {vscEntries.Count}");
        Require(vscEntries.Any(e => e.Keys == "Ctrl+Alt+F" && e.Action == "formatDocument"),
            "ctrl+alt+f 未转成 Ctrl+Alt+F / formatDocument");
        Require(vscEntries.Any(e => e.Keys == "Ctrl+Shift+Esc"), "ctrl+shift+escape 未转成 Ctrl+Shift+Esc");

        // --- JetBrains：keymap xml ---
        string idea = Path.Combine(appData.Path, "JetBrains", "IntelliJIdea2024.1", "keymaps");
        Directory.CreateDirectory(idea);
        File.WriteAllText(Path.Combine(idea, "my.xml"), """
            <keymap version="1" name="My">
              <action id="SaveAll">
                <keyboard-shortcut first-keystroke="control shift S" />
              </action>
              <action id="ReformatCode">
                <keyboard-shortcut first-keystroke="control alt L" second-keystroke="A" />
                <keyboard-shortcut first-keystroke="control alt K" />
              </action>
            </keymap>
            """);

        bool jbHit = JetBrainsAdapter.TryRead(
            @"C:/x/idea64.exe", appData.Path, out List<AdapterEntry> jbEntries, out string jbReason, out string product);
        Require(jbHit, $"JetBrains 适配器应命中：{jbReason}");
        Require(product == "IntelliJIdea2024.1", $"产品目录不符：{product}");
        Require(jbEntries.Count == 2, $"应读到 2 条（两段式跳过），实际 {jbEntries.Count}");
        Require(jbEntries.Any(e => e.Keys == "Ctrl+Shift+S" && e.Action == "SaveAll"),
            "control shift S 未转成 Ctrl+Shift+S");
        Require(jbEntries.Any(e => e.Keys == "Ctrl+Alt+K"), "control alt K 未转成 Ctrl+Alt+K");

        // --- 模板：必须能被仓库原样加载（注释与尾逗号） ---
        string templatePath = UserApps.WriteTemplate(userDir.Path, @"C://Windows//notepad.exe");
        Require(File.Exists(templatePath), "模板没写出来");
        string text = File.ReadAllText(templatePath);
        Require(text.Contains("//", StringComparison.Ordinal), "模板应带注释");
        Require(text.Contains("processNames", StringComparison.Ordinal), "模板应带 processNames");

        var repository = new ShortcutRepository(userDir.Path);
        Require(repository.LoadErrors.Count == 0,
            $"模板应能被加载，实际报错：{string.Join("；", repository.LoadErrors)}");
        AppShortcutProfile profile = repository.FindByProcess("notepad")
            ?? throw new ShellCheckException("模板里的 processNames 没被识别");
        Require(profile.Shortcuts.Count == 3, $"模板应含 3 条示例，实际 {profile.Shortcuts.Count}");

        return $"VS Code 2 条 / JetBrains 2 条（{product}）/ 模板 3 条可加载";
    }

    private sealed class TempDir : IDisposable
    {
        public TempDir()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), "hotkeyhud-shell-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Write(string fileName, string content)
            => File.WriteAllText(System.IO.Path.Combine(Path, fileName), content);

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch
            {
                // 清理失败不影响结论
            }
        }
    }

    private static string HookFlagsParsed()
    {
        var hits = new List<int>();
        using var hook = new LowLevelKeyboardHook(vk => vk is 0x28 or 0x26, vk => hits.Add(vk));

        // 合成 KBDLLHOOKSTRUCT：0 vkCode / 4 scanCode / 8 flags / 12 time
        IntPtr buffer = Marshal.AllocHGlobal(20);
        try
        {
            Marshal.WriteInt32(buffer, 0, 0x28);   // vkCode = VK_DOWN
            Marshal.WriteInt32(buffer, 4, 0x50);   // scanCode
            Marshal.WriteInt32(buffer, 12, 0);

            // 按下：flags = 0
            Marshal.WriteInt32(buffer, 8, 0);
            IntPtr downResult = hook.DispatchForTest(0, IntPtr.Zero, buffer);

            // 松开：flags = LLKHF_UP (0x80)
            Marshal.WriteInt32(buffer, 8, 0x80);
            IntPtr upResult = hook.DispatchForTest(0, IntPtr.Zero, buffer);

            Require(downResult == (IntPtr)1, "按下应被吞掉（返回 1）");
            Require(upResult == (IntPtr)1, "松开也必须吞掉（返回 1），否则前台会收到孤立 keyup");
            Require(hits.Count == 1, $"按下 + 松开应只通知 1 次翻页，实际 {hits.Count} 次");
            Require(hits[0] == 0x28, $"通知的虚拟键不符：0x{hits[0]:X}");

            // 非翻页键：既不吞也不通知
            Marshal.WriteInt32(buffer, 0, 0x41);
            IntPtr other = hook.DispatchForTest(0, IntPtr.Zero, buffer);
            Require(other != (IntPtr)1, "非翻页键不应被吞");
            Require(hits.Count == 1, "非翻页键不应触发翻页");
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }

        return "按下通知 1 次、松开不通知，两者都返回 1；非翻页键放行";
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
