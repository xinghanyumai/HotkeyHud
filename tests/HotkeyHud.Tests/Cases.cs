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
        Program.Run("Case06 面板显示时按 Ctrl+C 高亮并 500ms 后收起（v1.7）", Case06_KnownComboHidesImmediately);
        Program.Run("Case07 面板显示时按 Ctrl+K 高亮并 500ms 后收起", Case07_UnknownComboFlashes);
        Program.Run("Case08 熟记条目降权到「通用」组并排最后", Case08_KnownDeferredToGeneralGroup);
        Program.Run("Case09 只显示当前应用条目", Case09_OnlyForegroundAppEntries);
        Program.Run("Case10 未收录应用不混入其他条目", Case10_UnknownAppHasNoEntries);
        Program.Run("Case11 双修饰键条目不出现在单修饰键面板", Case11_MultiModifierExcluded);
        Program.Run("Case12 独占全屏抑制", Case12_ExclusiveFullscreenSuppressed);
        Program.Run("Case13 发行平台目录抑制", Case13_PlatformPathSuppressed);
        Program.Run("Case14 普通窗口不抑制", Case14_NormalWindowNotSuppressed);
        Program.Run("Case15 前台窗口切换立即收起", Case15_ForegroundChangedHides);
        Program.Run("Case16 前台切换后修饰键状态被清空", Case16_ForegroundChangeClearsModifiers);
        Program.Run("Case17 默认长按阈值为 450ms", Case17_DefaultDwellIs450);
        Program.Run("Case18 未收录应用：非 Win 走引导，Win 仍显示系统档案", Case18_UnrecordedGuidance);
        Program.Run("Case19 桌面不算游戏（Win+D 后不抑制）", Case19_DesktopNotSuppressed);
        Program.Run("Case20 用户档案覆盖内置同 keys 条目", Case20_UserOverridesBuiltin);
        Program.Run("Case21 用户条目不受「通用」降权影响", Case21_UserEntrySkipsDeferral);
        Program.Run("Case22 用户档案损坏不影响其他档案", Case22_BrokenUserFileIsolated);
    }

    // ---------------- §5 状态机 ----------------

    private static void Case01_DwellJustBelowThreshold()
    {
        var sm = new TriggerStateMachine(dwellMs: 300);
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
        var sm = new TriggerStateMachine(dwellMs: 300);
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
        var sm = new TriggerStateMachine(dwellMs: 300);
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
        var sm = new TriggerStateMachine(dwellMs: 300);
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
        var sm = new TriggerStateMachine(dwellMs: 300);
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
        // §4.3 v1.7：熟记名单不再抑制闪烁——Ctrl+C 与普通组合走同一条路
        var sm = new TriggerStateMachine(dwellMs: 300);
        int hides = 0;
        Combo? flashed = null;
        sm.HideRequested += () => hides++;
        sm.ComboFlashed += combo => flashed = combo;

        sm.OnKey(new KeyEvent(Keys.VkControl, true, 0));
        sm.OnTick(300);
        Check.Equal(HudState.Visible, sm.State, "前置条件：面板已显示");

        sm.OnKey(new KeyEvent(VkC, true, 400));

        Check.Equal(new Combo(Modifier.Ctrl, VkC), flashed, "Ctrl+C 应触发高亮");
        Check.Equal(0, hides, "高亮期间不应收起");
        Check.Equal(HudState.Flashing, sm.State, "应进入 Flashing");

        sm.OnTick(900);
        Check.Equal(1, hides, "500ms 后应收起");
        Check.Equal(HudState.Idle, sm.State, "应回到 Idle");
    }

    private static void Case07_UnknownComboFlashes()
    {
        var sm = new TriggerStateMachine(dwellMs: 300);
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

    private static void Case08_KnownDeferredToGeneralGroup()
    {
        var profile = Profile("test", "测试应用",
            Entry("Ctrl+C", "复制", "编辑"),
            Entry("Ctrl+S", "保存", "文件"),
            Entry("Ctrl+K", "链接", "编辑"));

        var first = HudBuilder.Build(profile, Modifier.Ctrl, new KnownShortcutFilter());

        Check.Equal(3, first.TotalCount, "v1.7：熟记条目保留，总数应为 3");
        Check.Equal(2, first.DeferredKnownCount, "被降权的应为 2");
        Check.Equal(2, first.PageCount, "通用组强制另起一页，共 2 页");

        // 第 1 页只有应用组；文件组只有一条 Ctrl+S，被降权后该组整体消失
        Check.Equal(1, first.Groups.Count, "第 1 页应只有「编辑」一组");
        Check.Equal("编辑", first.Groups[0].Title, "编辑组应排最前");
        Check.True(first.Groups[0].Items.Any(i => i.Keys == "K"), "编辑组应含 K");
        Check.False(first.Groups.Any(g => g.Title == HudBuilder.GeneralGroupTitle),
            "通用组不得出现在第 1 页");

        // 最后一页是通用组
        var last = HudBuilder.Build(profile, systemProfile: null, Modifier.Ctrl, new KnownShortcutFilter(), pageIndex: 1);
        Check.Equal(HudBuilder.GeneralGroupTitle, last.Groups[0].Title, "最后一页应是通用组");
        Check.Equal(2, last.Groups[0].Items.Count, "通用组应有 2 条");
        Check.True(last.Groups[0].Items.Any(i => i.Keys == "C"), "通用组应含 C");
        Check.True(last.Groups[0].Items.Any(i => i.Keys == "S"), "通用组应含 S");
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

        // v1.5：匹配规则改为「包含」——双修饰键条目要出现在 Ctrl 面板里
        Check.Equal(3, items.Count, "Ctrl 面板应包含全部含 Ctrl 的组合");
        Check.True(items.Any(i => i.Keys == "Shift+="), "应出现 Shift+=（键帽不带重复的 Ctrl）");
        Check.True(items.Any(i => i.Keys == "Alt+M"), "应出现 Alt+M");
        Check.True(items.All(i => !i.Keys.StartsWith("Ctrl", StringComparison.Ordinal)),
            "键帽不得再带 Ctrl 前缀");
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

        // §2.2 v1.3：D5 恒开，不再有开关。无边框全屏必须抑制——它是 QUNS 不上报全屏时的兜底
        var borderless = new GameSignal(
            ExclusiveFullscreen: false,
            CoversWholeMonitor: true,
            HasCaption: false,
            ProcessName: "notepad",
            WindowClass: "Notepad",
            ExecutablePath: @"C:\Program Files\WindowsApps\notepad.exe");

        Check.True(
            GameClassifier.ShouldSuppress(borderless, null, out string d5Reason),
            "无边框全屏必须抑制（D5 恒开）");
        Check.True(d5Reason.StartsWith("D5", StringComparison.Ordinal), $"命中原因应为 D5，实际 {d5Reason}");

        // 有标题栏的全屏窗口（最大化）不算 D5
        var maximized = borderless with { HasCaption = true };
        Check.False(
            GameClassifier.ShouldSuppress(maximized, null, out _),
            "有标题栏的全屏窗口不应按 D5 抑制");
    }

    // ---------------- 全局 ----------------

    private static void Case15_ForegroundChangedHides()
    {
        var sm = new TriggerStateMachine(dwellMs: 300);
        int hides = 0;
        sm.HideRequested += () => hides++;

        sm.OnKey(new KeyEvent(Keys.VkControl, true, 0));
        sm.OnTick(300);
        Check.Equal(HudState.Visible, sm.State, "前置条件：面板已显示");

        sm.OnForegroundChanged();

        Check.Equal(1, hides, "前台切换应立即收起");
        Check.Equal(HudState.Idle, sm.State, "应回到 Idle");

        // S2 整改第 2 项：前台切换会清空非修饰键，漏掉一次 key-up 不该造成永久阻塞
        var blocked = new TriggerStateMachine(dwellMs: 300);
        int shows = 0;
        blocked.ShowRequested += _ => shows++;

        blocked.OnKey(new KeyEvent(Keys.VkControl, true, 0));
        blocked.OnKey(new KeyEvent(VkC, true, 60)); // 取消并阻塞
        blocked.OnForegroundChanged();              // 非修饰键一律视为已松开
        blocked.OnKey(new KeyEvent(Keys.VkControl, false, 100));
        blocked.OnKey(new KeyEvent(Keys.VkControl, true, 200));
        blocked.OnTick(500);

        Check.Equal(1, shows, "前台切换后阻塞应可解除，否则一次丢失的 key-up 会让 HUD 永久失效");
    }

    private static void Case16_ForegroundChangeClearsModifiers()
    {
        var sm = new TriggerStateMachine(dwellMs: 300);
        int shows = 0;
        sm.ShowRequested += _ => shows++;

        // 按住 Ctrl 进入 Armed
        sm.OnKey(new KeyEvent(Keys.VkControl, true, 0));
        Check.Equal(HudState.Armed, sm.State, "前置条件：应处于 Armed");

        // 前台切换：物理键状态不可知，修饰键也一并视为松开
        sm.OnForegroundChanged();
        Check.Equal(HudState.Idle, sm.State, "前台切换后应回到 Idle");

        // 这一次 key-up 在真实场景里可能永远收不到；即使收到也不能出错
        sm.OnKey(new KeyEvent(Keys.VkControl, false, 50));

        // 再长按仍能正常弹出
        sm.OnKey(new KeyEvent(Keys.VkControl, true, 100));
        sm.OnTick(400);

        Check.Equal(1, shows, "修饰键状态被清空后，重新长按应能弹出");
        Check.Equal(HudState.Visible, sm.State, "应进入 Visible");
    }

    private static void Case20_UserOverridesBuiltin()
    {
        using var builtin = new TempDirectory();
        using var user = new TempDirectory();

        builtin.Write("test.json", """
            {
              "app": "test",
              "displayName": "测试应用",
              "processNames": ["TEST.EXE"],
              "shortcuts": [
                { "keys": "Ctrl+F", "action": "内置查找", "group": "编辑" },
                { "keys": "Ctrl+K", "action": "内置链接", "group": "编辑" }
              ]
            }
            """);

        user.Write("test.json", """
            {
              "app": "test",
              "displayName": "测试应用",
              "processNames": ["TEST.EXE"],
              "shortcuts": [
                { "keys": "Ctrl+F", "action": "我的查找", "group": "编辑" },
                { "keys": "Ctrl+G", "action": "我的跳转", "group": "编辑" }
              ]
            }
            """);

        var repository = new ShortcutRepository(builtin.Path, user.Path);
        AppShortcutProfile profile = repository.FindByProcess("test.exe")
            ?? throw new InvalidOperationException("用户档案没有合并进内置档案");

        Check.Equal(3, profile.Shortcuts.Count, "同 keys 应覆盖而不是追加（2 + 1 新增）");

        ShortcutEntry find = profile.Shortcuts.First(e => e.Keys == "Ctrl+F");
        Check.Equal("我的查找", find.Action, "同 keys 应由用户覆盖");
        Check.Equal(ShortcutSource.User, find.Source, "覆盖后的条目来源应为 user");
        Check.Equal(0, profile.Shortcuts.IndexOf(find), "覆盖应保留内置里的位置");

        ShortcutEntry jump = profile.Shortcuts.First(e => e.Keys == "Ctrl+G");
        Check.Equal(ShortcutSource.User, jump.Source, "新增条目来源应为 user");
        Check.Equal(1, repository.UserProfileCount, "应统计到 1 份用户档案");
    }

    private static void Case21_UserEntrySkipsDeferral()
    {
        using var builtin = new TempDirectory();
        using var user = new TempDirectory();

        builtin.Write("test.json", """
            {
              "app": "test",
              "displayName": "测试应用",
              "processNames": ["TEST.EXE"],
              "shortcuts": [
                { "keys": "Ctrl+C", "action": "内置复制", "group": "编辑" },
                { "keys": "Ctrl+K", "action": "内置链接", "group": "编辑" }
              ]
            }
            """);

        user.Write("test.json", """
            {
              "app": "test",
              "displayName": "测试应用",
              "processNames": ["TEST.EXE"],
              "shortcuts": [
                { "keys": "Ctrl+C", "action": "我的复制", "group": "编辑" }
              ]
            }
            """);

        var repository = new ShortcutRepository(builtin.Path, user.Path);
        var model = HudBuilder.Build(
            repository.FindByProcess("test"), Modifier.Ctrl, new KnownShortcutFilter());

        // 用户显式写的 Ctrl+C 留在应用组，不进「通用」
        Check.Equal(2, model.TotalCount, "两条都应在列表里");
        Check.Equal(0, model.DeferredKnownCount, "用户条目不应被降权");
        Check.False(model.Groups.Any(g => g.Title == HudBuilder.GeneralGroupTitle),
            "不应出现「通用」组");
        Check.True(model.Groups[0].Items.Any(i => i.Keys == "C" && i.Action == "我的复制"),
            "用户写的 Ctrl+C 应在应用组内");
    }

    private static void Case22_BrokenUserFileIsolated()
    {
        using var builtin = new TempDirectory();
        using var user = new TempDirectory();

        builtin.Write("test.json", """
            {
              "app": "test",
              "displayName": "测试应用",
              "processNames": ["TEST.EXE"],
              "shortcuts": [ { "keys": "Ctrl+K", "action": "链接", "group": "编辑" } ]
            }
            """);

        user.Write("broken.json", "{ this is not json");
        user.Write("_global.json", """
            { "app": "global", "displayName": "自定义", "shortcuts": [
                { "keys": "Ctrl+Alt+G", "action": "我的全局" }
            ] }
            """);

        var repository = new ShortcutRepository(builtin.Path, user.Path);

        Check.True(repository.LoadErrors.Count > 0, "损坏的用户档案应记入 LoadErrors");
        Check.True(repository.FindByProcess("test.exe") is not null, "内置档案不应被损坏文件连累");
        Check.True(repository.GlobalProfile is not null, "全局档案应加载成功");

        // 全局条目：组名缺省时归入「自定义」，且对任何应用可见
        var model = HudBuilder.Build(
            repository.FindByProcess("test"), systemProfile: null, Modifier.Ctrl,
            new KnownShortcutFilter(), globalProfile: repository.GlobalProfile);
        Check.True(model.Groups.Any(g => g.Title == "自定义"), "全局条目应归入「自定义」组");
    }

    private static void Case19_DesktopNotSuppressed()
    {
        // Win+D 之后前台是 Progman：铺满屏幕、无标题栏。若被 D5 当成无边框全屏，
        // 桌面上长按 Ctrl 就完全没反应（S1-B/S1-E 自检实测踩到）
        var desktop = new GameSignal(
            ExclusiveFullscreen: false,
            CoversWholeMonitor: true,
            HasCaption: false,
            ProcessName: "explorer",
            WindowClass: "Progman",
            ExecutablePath: @"C://Windows//explorer.exe");

        Check.False(GameClassifier.ShouldSuppress(desktop, null, out _), "桌面不应被抑制");

        // 真正的无边框全屏游戏仍然要抑制
        var game = desktop with { ProcessName = "game", WindowClass = "GameWnd" };
        Check.True(GameClassifier.ShouldSuppress(game, null, out _), "无边框全屏游戏仍应抑制");
    }

    private static void Case18_UnrecordedGuidance()
    {
        var system = Profile("system", "Windows", Entry("Win+D", "显示桌面"), Entry("Ctrl+Esc", "开始菜单"));
        var filter = new KnownShortcutFilter(Array.Empty<string>());

        // 未收录应用 + Ctrl：不拿系统档案的零星条目冒充内容（S7 返工 §3.2）
        var ctrl = HudBuilder.Build(null, system, Modifier.Ctrl, filter, "豆包.exe");
        Check.Equal(0, ctrl.TotalCount, "未收录应用的 Ctrl 面板应为空（走引导）");
        Check.False(ctrl.HasProfile, "未收录应用 HasProfile 应为 false");

        // 未收录应用 + Win：系统快捷键就是它的全部内容，必须保留
        var win = HudBuilder.Build(null, system, Modifier.Win, filter, "豆包.exe");
        Check.Equal(1, win.TotalCount, "Win 面板应显示系统档案的 Win 条目");
        Check.False(win.HasProfile, "Win 面板仍标记为未收录");

        // 已收录应用但该修饰键下没有条目：保留「该应用没有…」文案，不升级成引导
        var recorded = Profile("app", "某应用", Entry("Ctrl+K", "插入超链接"));
        var emptyShift = HudBuilder.Build(recorded, system, Modifier.Shift, filter, "某应用");
        Check.Equal(0, emptyShift.TotalCount, "Shift 面板应为空");
        Check.True(emptyShift.HasProfile, "已收录应用 HasProfile 应为 true");
    }

    private static void Case17_DefaultDwellIs450()
    {
        var sm = new TriggerStateMachine();
        Check.Equal(TriggerStateMachine.DefaultDwellMs, sm.DwellMs, "默认阈值应取自常量");
        Check.Equal(450, sm.DwellMs, "默认长按阈值应为 450ms（v1.5 由 300 上调）");

        int shows = 0;
        sm.ShowRequested += _ => shows++;

        sm.OnKey(new KeyEvent(Keys.VkControl, true, 0));
        sm.OnTick(449);
        Check.Equal(0, shows, "449ms 时不应显示");

        sm.OnTick(450);
        Check.Equal(1, shows, "450ms 时应显示");
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
