using HotkeyHud.Core;

namespace HotkeyHud.Tests;

/// <summary>
/// §7 的 15 条验收用例。用例只通过 Core 的公开接口驱动，不碰 App 层。
/// 状态机用例全部用手工推进的 long 时钟，不使用 Thread.Sleep。
/// </summary>
internal static class Cases
{
    private const int VkC = 0x43;
    private const int VkK = 0x4B;

    public static void RunAll()
    {
        Program.Run("Case01 按住 299ms 面板不出现", Case01_DwellJustBelowThreshold);
        Program.Run("Case02 按住 300ms 面板出现且标题为应用名", Case02_DwellAtThresholdShows);
        Program.Run("Case03 按住 90ms 后松开，全程无事件", Case03_ShortPressNeverShows);
        Program.Run("Case04 Ctrl 后 60ms 按 C，面板不出现", Case04_ComboPressCancelsArm);
        Program.Run("Case05 取消后全部松开再长按仍能弹出", Case05_CanRearmAfterCancel);
        Program.Run("Case06 面板显示时按 Ctrl+C 立即收起且不高亮", Case06_KnownComboHidesImmediately);
        Program.Run("Case07 面板显示时按 Ctrl+K 高亮并 500ms 后收起", Case07_UnknownComboFlashes);
        Program.Run("Case08 熟记名单同时作用于列表与计数", Case08_KnownFilteredFromList);
        Program.Run("Case09 只显示当前应用条目", Case09_OnlyForegroundAppEntries);
        Program.Run("Case10 未收录应用不混入其他条目", Case10_UnknownAppHasNoEntries);
        Program.Run("Case11 双修饰键条目不出现在单修饰键面板", Case11_MultiModifierExcluded);
        Program.Run("Case12 独占全屏抑制", Case12_ExclusiveFullscreenSuppressed);
        Program.Run("Case13 发行平台目录抑制", Case13_PlatformPathSuppressed);
        Program.Run("Case14 普通窗口不抑制", Case14_NormalWindowNotSuppressed);
        Program.Run("Case15 前台窗口切换立即收起", Case15_ForegroundChangedHides);
    }

    // ---------------- §5 状态机 ----------------

    private static void Case01_DwellJustBelowThreshold()
    {
        var sm = new TriggerStateMachine();
        int shows = 0;
        sm.ShowRequested += _ => shows++;

        sm.OnKey(new KeyEvent(Keys.VkControl, true, 0));
        sm.OnTick(299);

        Check.Equal(0, shows, "299ms 时不应请求显示");
        Check.Equal(HudState.Armed, sm.State, "299ms 时应仍处于 Armed");

        // 阈值范围校验（S2 整改第 1 项）：越界必须抛，不许静默接受
        Check.Throws<ArgumentOutOfRangeException>(
            () => new TriggerStateMachine(dwellMs: 79), "DwellMs 低于下界应抛异常");
        Check.Throws<ArgumentOutOfRangeException>(
            () => new TriggerStateMachine(dwellMs: 5001), "DwellMs 高于上界应抛异常");
        Check.Throws<ArgumentOutOfRangeException>(
            () => new TriggerStateMachine(flashMs: 0), "FlashMs 越界应抛异常");
        Check.Throws<ArgumentOutOfRangeException>(
            () => new TriggerStateMachine { DwellMs = 10000 }, "DwellMs 设值越界应抛异常");
    }

    private static void Case02_DwellAtThresholdShows()
    {
        var sm = new TriggerStateMachine();
        var shown = new List<Modifier>();
        sm.ShowRequested += m => shown.Add(m);

        sm.OnKey(new KeyEvent(Keys.VkControl, true, 0));
        sm.OnTick(300);

        Check.Equal(1, shown.Count, "300ms 时应请求显示一次");
        Check.Equal(Modifier.Ctrl, shown[0], "触发键应为 Ctrl");
        Check.Equal(HudState.Visible, sm.State, "应进入 Visible");

        // 触发键自动重复：同一个 Ctrl 仍按住时系统会持续发 down
        sm.OnKey(new KeyEvent(Keys.VkControl, true, 310));
        Check.Equal(HudState.Visible, sm.State, "触发键自动重复不应把面板打掉");
        Check.Equal(1, shown.Count, "自动重复不应重复请求显示");
    }

    private static void Case03_ShortPressNeverShows()
    {
        var sm = new TriggerStateMachine();
        int shows = 0;
        int hides = 0;
        sm.ShowRequested += _ => shows++;
        sm.HideRequested += () => hides++;

        sm.OnKey(new KeyEvent(Keys.VkControl, true, 0));
        sm.OnKey(new KeyEvent(Keys.VkControl, false, 90));
        sm.OnTick(90);
        sm.OnTick(1000);

        Check.Equal(0, shows, "短按不应请求显示");
        Check.Equal(0, hides, "从未显示过就不应请求收起");
        Check.Equal(HudState.Idle, sm.State, "应回到 Idle 且无残留状态");
    }

    private static void Case04_ComboPressCancelsArm()
    {
        var sm = new TriggerStateMachine();
        int shows = 0;
        sm.ShowRequested += _ => shows++;

        sm.OnKey(new KeyEvent(Keys.VkControl, true, 0));
        sm.OnKey(new KeyEvent(VkC, true, 60));
        sm.OnTick(60);
        sm.OnTick(400);

        Check.Equal(0, shows, "按下具体组合后不应再弹出");
        Check.Equal(HudState.Idle, sm.State, "应取消并停在 Idle");
    }

    private static void Case05_CanRearmAfterCancel()
    {
        var sm = new TriggerStateMachine();
        int shows = 0;
        sm.ShowRequested += _ => shows++;

        // 承接 Case04：Ctrl 按下 → C 按下 → 取消并阻塞
        sm.OnKey(new KeyEvent(Keys.VkControl, true, 0));
        sm.OnKey(new KeyEvent(VkC, true, 60));

        // 阻塞必须真的存在：Ctrl 还按着，推进多久都不能弹
        sm.OnTick(400);
        sm.OnTick(2000);
        Check.Equal(0, shows, "阻塞期间即使继续按住也不得弹出");

        // 只松开 Ctrl、C 仍按住 → 按「阻塞至全部松开」的字面义仍应阻塞
        sm.OnKey(new KeyEvent(Keys.VkControl, false, 130));
        sm.OnKey(new KeyEvent(Keys.VkControl, true, 150));
        sm.OnTick(450);
        Check.Equal(0, shows, "非修饰键未松开时不得解除阻塞");

        // 全部松开
        sm.OnKey(new KeyEvent(Keys.VkControl, false, 170));
        sm.OnKey(new KeyEvent(VkC, false, 180));

        // 重新长按 300ms
        sm.OnKey(new KeyEvent(Keys.VkControl, true, 200));
        sm.OnTick(500);

        Check.Equal(1, shows, "全部松开后阻塞应解除，能重新弹出");
        Check.Equal(HudState.Visible, sm.State, "应进入 Visible");
    }

    private static void Case06_KnownComboHidesImmediately()
    {
        var filter = new KnownShortcutFilter();
        var sm = new TriggerStateMachine { FlashPredicate = combo => !filter.IsKnown(combo) };
        int hides = 0;
        int flashes = 0;
        sm.HideRequested += () => hides++;
        sm.ComboFlashed += _ => flashes++;

        sm.OnKey(new KeyEvent(Keys.VkControl, true, 0));
        sm.OnTick(300);
        Check.Equal(HudState.Visible, sm.State, "前置条件：面板已显示");

        sm.OnKey(new KeyEvent(VkC, true, 400));

        Check.Equal(1, hides, "Ctrl+C 应立即收起");
        Check.Equal(0, flashes, "Ctrl+C 不应触发高亮");
        Check.Equal(HudState.Idle, sm.State, "应直接回 Idle，不能停留 500ms");
    }

    private static void Case07_UnknownComboFlashes()
    {
        var filter = new KnownShortcutFilter();
        var sm = new TriggerStateMachine { FlashPredicate = combo => !filter.IsKnown(combo) };
        int hides = 0;
        Combo? flashed = null;
        sm.HideRequested += () => hides++;
        sm.ComboFlashed += combo => flashed = combo;

        sm.OnKey(new KeyEvent(Keys.VkControl, true, 0));
        sm.OnTick(300);
        sm.OnKey(new KeyEvent(VkK, true, 400));

        Check.Equal(new Combo(Modifier.Ctrl, VkK), flashed, "应高亮 Ctrl+K");
        Check.Equal(HudState.Flashing, sm.State, "应进入 Flashing");

        sm.OnTick(899);
        Check.Equal(0, hides, "未到 500ms 不应收起");

        sm.OnTick(900);
        Check.Equal(1, hides, "500ms 后应收起");
        Check.Equal(HudState.Idle, sm.State, "应回到 Idle");
    }

    // ---------------- §4 / §3 构建 ----------------

    private static void Case08_KnownFilteredFromList()
    {
        var profile = Profile("test", "测试应用",
            Entry("Ctrl+C", "复制"),
            Entry("Ctrl+S", "保存"),
            Entry("Ctrl+K", "链接"));

        var model = HudBuilder.Build(profile, Modifier.Ctrl, new KnownShortcutFilter());
        var items = model.Groups.SelectMany(g => g.Items).ToList();

        Check.Equal(1, model.TotalCount, "可见条目数应为 1");
        Check.Equal(2, model.HiddenByKnownCount, "被熟记名单剔除的应为 2");
        Check.Equal(1, items.Count, "列表里只应剩一条");
        Check.Equal("Ctrl+K", items[0].Keys, "剩下的应是 Ctrl+K");
    }

    private static void Case09_OnlyForegroundAppEntries()
    {
        using var dir = new TempDirectory();
        dir.Write("word.json", """
            {
              "app": "winword",
              "displayName": "Word",
              "processNames": ["WINWORD.EXE"],
              "shortcuts": [
                { "keys": "Ctrl+K", "action": "插入超链接", "group": "插入" }
              ]
            }
            """);
        dir.Write("chrome.json", """
            {
              "app": "chrome",
              "displayName": "Chrome",
              "processNames": ["chrome.exe"],
              "shortcuts": [
                { "keys": "Ctrl+T", "action": "打开新标签页" }
              ]
            }
            """);

        var repo = new ShortcutRepository(dir.Path);
        Check.Equal(2, repo.ProfileCount, "应加载两个档案");

        var profile = repo.FindByProcess("WINWORD.EXE");
        Check.NotNull(profile, "应能按进程名查到 Word");
        Check.Equal("Word", profile!.DisplayName, "档案名应为 Word");

        var model = HudBuilder.Build(profile, Modifier.Ctrl, new KnownShortcutFilter(Array.Empty<string>()));
        var actions = model.Groups.SelectMany(g => g.Items).Select(i => i.Action).ToList();

        Check.True(actions.Contains("插入超链接"), "应包含 Word 条目");
        Check.False(actions.Contains("打开新标签页"), "不得混入 Chrome 任何条目");
    }

    private static void Case10_UnknownAppHasNoEntries()
    {
        using var dir = new TempDirectory();
        dir.Write("word.json", """
            {
              "app": "winword",
              "displayName": "Word",
              "processNames": ["WINWORD.EXE"],
              "shortcuts": [
                { "keys": "Ctrl+K", "action": "插入超链接" }
              ]
            }
            """);

        var repo = new ShortcutRepository(dir.Path);
        var profile = repo.FindByProcess("notepad.exe");
        Check.True(profile is null, "未收录进程应查不到档案");

        var model = HudBuilder.Build(
            profile, Modifier.Ctrl, new KnownShortcutFilter(Array.Empty<string>()), "记事本");

        Check.False(model.HasProfile, "HasProfile 应为 false");
        Check.Equal(0, model.TotalCount, "不应有任何条目");
        Check.Equal(0, model.Groups.Count, "不得混入其他应用的条目");
        Check.Equal("记事本", model.AppName, "标题应回落到调用方给的应用名");
    }

    private static void Case11_MultiModifierExcluded()
    {
        var profile = Profile("test", "测试应用",
            Entry("Ctrl+Shift+=", "双修饰一"),
            Entry("Ctrl+Alt+M", "双修饰二"),
            Entry("Ctrl+K", "正常条目"));

        var model = HudBuilder.Build(profile, Modifier.Ctrl, new KnownShortcutFilter(Array.Empty<string>()));
        var items = model.Groups.SelectMany(g => g.Items).ToList();

        Check.Equal(1, model.TotalCount, "Ctrl 面板只应有 1 条");
        Check.Equal("Ctrl+K", items[0].Keys, "双修饰键条目必须被排除");
    }

    // ---------------- §2 游戏判定 ----------------

    private static void Case12_ExclusiveFullscreenSuppressed()
    {
        var signal = new GameSignal(
            ExclusiveFullscreen: true,
            CoversWholeMonitor: false,
            HasCaption: true,
            ProcessName: "notepad",
            WindowClass: "Notepad",
            ExecutablePath: @"C:\Windows\notepad.exe");

        Check.True(GameClassifier.ShouldSuppress(signal, null, out string reason), "独占全屏应抑制");
        Check.True(reason.Length > 0, "抑制时必须给出命中原因");
    }

    private static void Case13_PlatformPathSuppressed()
    {
        var signal = new GameSignal(
            ExclusiveFullscreen: false,
            CoversWholeMonitor: false,
            HasCaption: true,
            ProcessName: "somegame",
            WindowClass: "SomeClass",
            ExecutablePath: @"D:\SteamLibrary\steamapps\common\Foo\foo.exe");

        Check.True(GameClassifier.ShouldSuppress(signal, null, out string reason), "发行平台目录应抑制");
        Check.True(reason.Length > 0, "抑制时必须给出命中原因");
    }

    private static void Case14_NormalWindowNotSuppressed()
    {
        var signal = new GameSignal(
            ExclusiveFullscreen: false,
            CoversWholeMonitor: false,
            HasCaption: true,
            ProcessName: "notepad",
            WindowClass: "Notepad",
            ExecutablePath: @"C:\Program Files\WindowsApps\notepad.exe");

        Check.False(GameClassifier.ShouldSuppress(signal, null, out _), "普通记事本窗口不应抑制");
    }

    // ---------------- 全局 ----------------

    private static void Case15_ForegroundChangedHides()
    {
        var sm = new TriggerStateMachine();
        int hides = 0;
        sm.HideRequested += () => hides++;

        sm.OnKey(new KeyEvent(Keys.VkControl, true, 0));
        sm.OnTick(300);
        Check.Equal(HudState.Visible, sm.State, "前置条件：面板已显示");

        sm.OnForegroundChanged();

        Check.Equal(1, hides, "前台切换应立即收起");
        Check.Equal(HudState.Idle, sm.State, "应回到 Idle");
    }

    // ---------------- 辅助 ----------------

    private static AppShortcutProfile Profile(
        string app, string displayName, params ShortcutEntry[] shortcuts)
        => new()
        {
            App = app,
            DisplayName = displayName,
            ProcessNames = new[] { app.ToUpperInvariant() + ".EXE" },
            Shortcuts = shortcuts.ToList(),
        };

    private static ShortcutEntry Entry(string keys, string action, string? group = null)
    {
        bool parsed = Keys.TryParse(keys, out Combo combo);
        return new ShortcutEntry
        {
            Keys = keys,
            Action = action,
            Group = group,
            Combo = combo,
            IsValid = parsed,
        };
    }
}
