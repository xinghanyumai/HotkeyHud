using System.Diagnostics;
using HotkeyHud.App.Platform;
using HotkeyHud.App.Shell;
using HotkeyHud.App.UI;
using HotkeyHud.Core;
using KeyNames = HotkeyHud.Core.Keys;

namespace HotkeyHud.App;

/// <summary>
/// 编排：Raw Input → 状态机 → 覆盖窗。S2.5 只做接线与时序，渲染仍是 S1 的单行文本。
/// <para>
/// 时钟唯一来源 = <see cref="RawInputSource.NowMs"/>。状态机的 KeyEvent 时间戳与 OnTick
/// 的 nowMs 都来自同一个 Stopwatch，混用会导致驻留判定直接失效。
/// </para>
/// </summary>
public sealed class HudApp : IDisposable
{
    private const int TimerIntervalMs = 20;
    private const int SuppressPollIntervalMs = 2000;
    private const int OverlayAlpha = 235;

    private readonly RawInputSource _input;
    private readonly OverlayForm _overlay;
    private readonly ShortcutRepository _repository;
    private KnownShortcutFilter _known;
    private readonly ForegroundProbe _probe = new();
    private readonly ForegroundWatcher _watcher;
    private readonly SuppressionProbe _suppressionProbe = new();
    private readonly TriggerStateMachine _machine;
    private readonly System.Windows.Forms.Timer _timer;
    private readonly System.Windows.Forms.Timer _suppressTimer;

    private const int MaxVisibleMs = 15_000;

    private HudModel? _currentModel;
    private LowLevelKeyboardHook? _pageHook;
    private int _pageIndex;
    private long _shownAtMs;
    private bool _manualPause;
    private bool _gameSuppressed;
    private bool _disposed;

    /// <summary>用户档案目录的默认位置（§3.4.1）。</summary>
    public static string DefaultUserAppsDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "HotkeyHud", "apps");

    /// <param name="appsDirectory">内置档案目录；null = 随 exe 输出的 Data\apps。</param>
    /// <param name="gameRules">抑制规则，null = 用配置里的名单。</param>
    /// <param name="config">配置，null = 默认值。</param>
    /// <param name="userAppsDirectory">
    /// 用户档案目录。null 且 <paramref name="appsDirectory"/> 也为 null（生产路径）时用
    /// <see cref="DefaultUserAppsDirectory"/>；显式给了内置目录（测试）则不加载用户档案，
    /// 避免测试被真实 %APPDATA% 里的文件污染。
    /// </param>
    public HudApp(
        string? appsDirectory = null,
        GameRules? gameRules = null,
        AppConfig? config = null,
        string? userAppsDirectory = null)
    {
        string directory = appsDirectory
                           ?? Path.Combine(AppContext.BaseDirectory, "Data", "apps");
        string? userDirectory = userAppsDirectory
                                ?? (appsDirectory is null ? DefaultUserAppsDirectory : null);

        Config = config ?? new AppConfig();
        GameRules = gameRules ?? new GameRules { GameProcessNames = Config.ExtraGameProcesses };
        _known = new KnownShortcutFilter(Config.KnownShortcuts);

        _repository = new ShortcutRepository(directory, userDirectory);
        // 先建会抛异常的那个，避免它失败时已经占着 Raw Input 窗口与 HGlobal
        _watcher = new ForegroundWatcher();
        _input = new RawInputSource();
        _overlay = new OverlayForm();
        _machine = new TriggerStateMachine(Config.DwellMs);
        _timer = new System.Windows.Forms.Timer { Interval = TimerIntervalMs };
        _suppressTimer = new System.Windows.Forms.Timer { Interval = SuppressPollIntervalMs };

        _input.Input += OnInput;
        _watcher.ForegroundChanged += OnForegroundChanged;
        _machine.ShowRequested += OnShowRequested;
        _machine.ComboFlashed += OnComboFlashed;
        _machine.HideRequested += OnHideRequested;
        _machine.PageRequested += OnPageRequested;
        _overlay.PageRequested += OnPageRequested;
        _timer.Tick += OnTimerTick;
        _suppressTimer.Tick += OnSuppressTick;

        RefreshSuppression();
        _suppressTimer.Start();

        if (ShowDiagnostics.Enabled)
        {
            int own = IntegrityLevel.OfCurrentProcess();
            ShowDiagnostics.Write(
                "start",
                ("pid", Environment.ProcessId.ToString()),
                ("integrity", $"{IntegrityLevel.Name(own)}(0x{own:X})"),
                ("profiles", _repository.ProfileCount.ToString()),
                ("dwellMs", Config.DwellMs.ToString()),
                ("log", ShowDiagnostics.LogPath));
        }
    }

    public RawInputSource Input => _input;

    /// <summary>档案仓库（设置页要用同一份，才能看到内置 + 用户合并后的结果）。</summary>
    public ShortcutRepository Repository => _repository;

    public OverlayForm Overlay => _overlay;

    public bool IsOverlayVisible { get; private set; }

    /// <summary>面板当前显示的文字摘要（供集成测试断言，不参与渲染）。</summary>
    public string CurrentLabel { get; private set; } = string.Empty;

    /// <summary>当前高亮的组合，无高亮为 null。</summary>
    public Combo? HighlightedCombo { get; private set; }

    /// <summary>计时器是否在跑。空闲时必须为 false（§八 空载 CPU 要求）。</summary>
    public bool TimerRunning => _timer.Enabled;

    /// <summary>状态机当前状态，供集成测试诊断用。</summary>
    public HudState State => _machine.State;

    /// <summary>手动暂停（§1 的 G1）。与游戏抑制并列，两者任一为真即抑制。</summary>
    public bool Suppressed
    {
        get => _manualPause;
        set
        {
            _manualPause = value;
            ApplySuppression();
        }
    }

    /// <summary>游戏 / 全屏抑制的当前判定（§2.2）。只读，由采集结果驱动。</summary>
    public bool GameSuppressed => _gameSuppressed;

    /// <summary>当前面板模型（含分页信息），验收用。</summary>
    public HudModel? CurrentModel => _currentModel;

    /// <summary>外部请求翻页，等价于用户按 ↑/↓。验收用它模拟钩子投递。</summary>
    public void RequestPage(int delta) => _machine.RequestPage(delta);

    /// <summary>翻页钩子当前是否装着。验收用它区分「多页装了」与「单页没装」。</summary>
    public bool IsPageHookInstalled => _pageHook?.IsInstalled == true;

    /// <summary>
    /// 钩子通路投递的翻页请求数。验收用：一次按键应只 +1——
    /// 修复前按下与松开都会投递，这里是 2。
    /// </summary>
    public int HookPageRequests { get; private set; }

    /// <summary>最近一次抑制判定的命中原因，空串表示未抑制。</summary>
    public string SuppressReason { get; private set; } = string.Empty;

    /// <summary>最近一次读到的系统通知状态原值（2/3/4 会触发 D1），仅用于诊断。</summary>
    public int NotificationState => _suppressionProbe.LastNotificationState;

    public GameRules GameRules { get; private set; }

    public AppConfig Config { get; private set; }

    /// <summary>前台进程名，托盘状态行用。</summary>
    public string ForegroundApp => _probe.ProcessName;

    /// <summary>
    /// 重载配置与档案：仓库重新读盘、熟记名单重建、长按阈值重设。
    /// 只在这里改这三样，避免「读了但没接上」。
    /// </summary>
    public void ReloadData(AppConfig config)
    {
        Config = config;
        GameRules = new GameRules { GameProcessNames = config.ExtraGameProcesses };

        _repository.Reload();
        _known = new KnownShortcutFilter(config.KnownShortcuts);
        _machine.DwellMs = config.DwellMs;

        RefreshSuppression();
    }

    public void ShowOverlay()
    {
        if (_machine.Suppressed)
        {
            return;
        }

        // S1 自检与打字冒烟依赖这条最简渲染路径，S3 不改它
        _overlay.ShowNoActivate(
            DebugPanelBounds(), Color.FromArgb(OverlayAlpha, 0x4C, 0x8D, 0xFF), "HUD · S1 调试");
        IsOverlayVisible = true;
        CurrentLabel = "HUD · S1 调试";
    }

    public void HideOverlay()
    {
        _overlay.HideOverlay();
        IsOverlayVisible = false;
        HighlightedCombo = null;
    }

    private void OnInput(InputEvent evt)
    {
        if (evt.Kind == InputKind.Key)
        {
            LogKey(evt);
            _machine.OnKey(new KeyEvent(evt.Code, evt.IsDown, evt.TimestampMs));

            if (ShowDiagnostics.Enabled)
            {
                ShowDiagnostics.Write(
                    "after-key",
                    ("state", _machine.State.ToString()),
                    ("held", _machine.HeldModifiers.ToString()),
                    ("blocked", _machine.IsBlocked.ToString().ToLowerInvariant()),
                    ("suppressed", _machine.Suppressed.ToString().ToLowerInvariant()),
                    ("timerRunning", _timer.Enabled.ToString().ToLowerInvariant()));
            }
        }
        else
        {
            _machine.OnMouseButton();
        }

        SyncTimer();
    }

    /// <summary>
    /// 诊断日志：记录修饰键与非修饰键的按下。这一段只观察——
    /// 「有没有 mod 行」就是「Raw Input 有没有收到」的判据。
    /// </summary>
    private void LogKey(InputEvent evt)
    {
        if (!ShowDiagnostics.Enabled)
        {
            return;
        }

        Modifier modifier = KeyNames.ModifierOf(evt.Code);
        if (modifier == Modifier.None)
        {
            ShowDiagnostics.Write(
                "nonmod",
                ("vk", $"0x{evt.Code:X2}"),
                ("down", evt.IsDown.ToString().ToLowerInvariant()),
                ("state", _machine.State.ToString()),
                ("processName", _probe.ProcessName));
            return;
        }

        ShowDiagnostics.Write(
            "mod",
            ("key", modifier.ToString()),
            ("down", evt.IsDown.ToString().ToLowerInvariant()),
            ("state", _machine.State.ToString()),
            ("processName", _probe.ProcessName),
            ("held", _machine.HeldModifiers.ToString()),
            ("blocked", _machine.IsBlocked.ToString().ToLowerInvariant()),
            ("suppressed", _machine.Suppressed.ToString().ToLowerInvariant()),
            ("nowMs", evt.TimestampMs.ToString()));
    }

    private void OnForegroundChanged()
    {
        _probe.Invalidate();
        _suppressionProbe.Invalidate();

        // 前台切换会清空 heldModifiers（S2.5 整改）。若某个应用频繁触发前台事件，
        // 长按就永远攒不满阈值——这条日志是唯一能看出该现象的地方。
        if (ShowDiagnostics.Enabled)
        {
            int fg = IntegrityLevel.OfForeground();
            int own = IntegrityLevel.OfCurrentProcess();
            ShowDiagnostics.Write(
                "fg",
                ("processName", _probe.ProcessName),
                ("state", _machine.State.ToString()),
                ("heldCleared", "true"),
                ("fgIntegrity", IntegrityLevel.Name(fg)),
                ("uipiBlocked", (fg > own && own != 0).ToString().ToLowerInvariant()));
        }

        _machine.OnForegroundChanged();
        SyncTimer();
        RefreshSuppression();
    }

    private int _beatTicks;

    private void OnSuppressTick(object? sender, EventArgs e)
    {
        RefreshSuppression();

        // 60 秒一行心跳：日志里有 beat 却没有 mod，说明 Raw Input 没收到键，而不是程序没跑
        if (ShowDiagnostics.Enabled && ++_beatTicks >= 30)
        {
            _beatTicks = 0;
            ShowDiagnostics.Write(
                "beat",
                ("processName", _probe.ProcessName),
                ("state", _machine.State.ToString()),
                ("suppressed", _machine.Suppressed.ToString().ToLowerInvariant()),
                ("foregroundIntegrity", IntegrityLevel.Name(IntegrityLevel.OfForeground())));
        }
    }

    /// <summary>
    /// 重新采集并判定是否抑制。只在「前台变化」与「2 秒轮询」两个时机调用——
    /// 绝不在 ShowRequested 里做，那是按键路径，必须零 I/O（§2.2）。
    /// </summary>
    private void RefreshSuppression()
    {
        try
        {
            GameSignal signal = _suppressionProbe.Capture();
            bool wasSuppressed = _gameSuppressed;
            _gameSuppressed = GameClassifier.ShouldSuppress(signal, GameRules, out string reason);
            SuppressReason = reason;

            if (ShowDiagnostics.Enabled && wasSuppressed != _gameSuppressed)
            {
                ShowDiagnostics.Write(
                    "suppress",
                    ("value", _gameSuppressed.ToString().ToLowerInvariant()),
                    ("reason", reason.Length > 0 ? reason : "-"),
                    ("processName", _probe.ProcessName),
                    ("covers", signal.CoversWholeMonitor.ToString().ToLowerInvariant()),
                    ("caption", signal.HasCaption.ToString().ToLowerInvariant()),
                    ("quns", _suppressionProbe.LastNotificationState.ToString()));
            }

            ApplySuppression();
        }
        catch (Exception ex)
        {
            // 采集失败不能连累输入通路，退化为「不抑制」并记一笔
            Debug.WriteLine($"[HotkeyHud] 抑制采集异常：{ex}");
        }
    }

    /// <summary>两个抑制来源取并集：手动暂停与游戏抑制互不覆盖。</summary>
    private void ApplySuppression()
    {
        _machine.Suppressed = _manualPause || _gameSuppressed;
        SyncTimer();
    }

    private void OnTimerTick(object? sender, EventArgs e)
    {
        // 计时器回调抛异常会变成 WinForms 未处理异常，整程序退出
        try
        {
            _machine.OnTick(_input.NowMs);
            SyncTimer();

            // 安全阀：key-up 丢失会让面板一直挂着，钩子就一直吞上下键——这比不能翻页严重得多
            if (IsOverlayVisible)
            {
                if (_input.NowMs - _shownAtMs > MaxVisibleMs)
                {
                    _machine.Reset();
                    _overlay.HideOverlay();
                    IsOverlayVisible = false;
                    UpdatePageHook();
                }
                else
                {
                    _overlay.RefreshAnchor();
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[HotkeyHud] tick 异常：{ex}");
        }
    }

    private void OnShowRequested(Modifier modifier)
    {
        if (_machine.Suppressed)
        {
            LogShow(modifier, suppressed: true, model: null);
            return;
        }

        // 仓库在启动时已 Reload，这里只查内存，不做 I/O（§六 禁止 3）
        _pageIndex = 0;
        _shownAtMs = _input.NowMs;
        BuildAndRender(modifier);
        LogShow(modifier, suppressed: false, model: _currentModel);
    }

    /// <summary>
    /// 诊断日志：ShowRequested 的完整判定链。字段顺序照 S7 返工文档 §1.1。
    /// 走到这里说明「状态机已触发」，所以本行存在与否就区分了「没触发」与「触发了但没显示」。
    /// </summary>
    private void LogShow(Modifier modifier, bool suppressed, HudModel? model)
    {
        if (!ShowDiagnostics.Enabled)
        {
            return;
        }

        string processName = _probe.ProcessName;
        bool profileHit = _repository.FindByProcess(processName) is not null;
        Rectangle rect = _overlay.LastBounds;
        string rectText = rect.IsEmpty
            ? "none"
            : $"{rect.Left},{rect.Top},{rect.Right},{rect.Bottom}";

        ShowDiagnostics.Write(
            "show",
            ("processName", processName.Length > 0 ? processName : "(空)"),
            ("profileHit", profileHit.ToString().ToLowerInvariant()),
            ("modifier", modifier.ToString()),
            ("total", (model?.TotalCount ?? -1).ToString()),
            ("pageCount", (model?.PageCount ?? -1).ToString()),
            ("suppressed", suppressed.ToString().ToLowerInvariant()),
            ("reason", SuppressReason.Length > 0 ? SuppressReason : "-"),
            ("armedAt", _machine.ArmedAtMs.ToString()),
            ("nowMs", _input.NowMs.ToString()),
            ("elapsedSinceArm", (_input.NowMs - _machine.ArmedAtMs).ToString()),
            ("overlayRect", rectText));
    }

    private void BuildAndRender(Modifier modifier, bool reanchor = true)
    {
        string processName = _probe.ProcessName;
        string displayName = processName.Length > 0 ? processName : "未知进程";

        _currentModel = HudBuilder.Build(
            _repository.FindByProcess(processName),
            _repository.SystemProfile,
            modifier,
            _known,
            displayName,
            _pageIndex,
            _repository.GlobalProfile);

        RenderModel(_currentModel, null, reanchor);
        UpdatePageHook();
    }

    /// <summary>
    /// 翻页。边界夹住，不循环；翻页不改变状态，仍是 Visible。
    /// 两条通路都汇到这里：钩子吞键后投递的消息，以及 Raw Input 送进状态机的 ↑/↓。
    /// </summary>
    private void OnPageRequested(int delta)
    {
        if (_currentModel is null || _currentModel.PageCount <= 1)
        {
            return;
        }

        int next = Math.Clamp(_currentModel.PageIndex + delta, 0, _currentModel.PageCount - 1);
        if (next == _currentModel.PageIndex)
        {
            return; // 已在边界：不动，也不循环
        }

        _pageIndex = next;

        // 翻页只重绘内容：尺寸由格数预算固定，重算位置会让面板跳一下
        BuildAndRender(_currentModel.Modifier, reanchor: false);
    }

    /// <summary>
    /// 只在「多页且面板可见」时装钩子（§2.3）。单页完全不装——绝大多数时候用户感觉不到输入被拦截。
    /// </summary>
    private void UpdatePageHook()
    {
        bool needed = IsOverlayVisible && _currentModel is { PageCount: > 1 };

        if (needed && _pageHook is null)
        {
            _pageHook = new LowLevelKeyboardHook(ShouldSwallowPageKey, OnPageKeySwallowed);
            _pageHook.Install();
            return;
        }

        if (!needed && _pageHook is not null)
        {
            _pageHook.Dispose();
            _pageHook = null;
        }
    }

    private static bool ShouldSwallowPageKey(int virtualKey)
        => virtualKey is KeyNames.VkUp or KeyNames.VkDown;

    /// <summary>钩子吞掉翻页键后的回调：只投递消息，绝不在钩子里渲染。</summary>
    private void OnPageKeySwallowed(int virtualKey)
    {
        HookPageRequests++;
        PostPageRequest(virtualKey == KeyNames.VkDown ? 1 : -1);
    }

    /// <summary>
    /// 钩子回调里只做一件事：把翻页意图投递出去。真正的翻页在消息循环里做，
    /// 避免在钩子里渲染（钩子有超时限制，超时会被系统静默卸掉）。
    /// </summary>
    private void PostPageRequest(int delta)
    {
        if (IsOverlayVisible && _overlay.IsHandleCreated)
        {
            NativeMethods.PostMessage(_overlay.Handle, OverlayForm.WmPageRequest, delta, 0);
        }
    }

    private void OnComboFlashed(Combo combo)
    {
        if (_machine.Suppressed || _currentModel is null)
        {
            return;
        }

        // 高亮只换颜色与背景，尺寸仍由同一份模型决定，不会重建 DIB
        RenderModel(_currentModel, combo, reanchor: false);
    }

    private void OnHideRequested()
    {
        if (ShowDiagnostics.Enabled)
        {
            ShowDiagnostics.Write(
                "hide",
                ("processName", _probe.ProcessName),
                ("state", _machine.State.ToString()));
        }

        _overlay.HideOverlay();
        IsOverlayVisible = false;
        HighlightedCombo = null;
        UpdatePageHook(); // 收起即卸钩，不延迟
    }

    private void RenderModel(HudModel model, Combo? highlight, bool reanchor = true)
    {
        _overlay.ShowModel(model, highlight, reanchor);
        IsOverlayVisible = true;
        HighlightedCombo = highlight;

        CurrentLabel = model.HasProfile
            ? $"{model.AppName} · {model.TotalCount} 条 {model.Modifier} 快捷键"
            : $"未收录 {model.AppName}";
    }

    /// <summary>§3.2 计时器按需启停：只有 Armed / Flashing 需要 tick，Idle 必须停。</summary>
    private void SyncTimer()
    {
        // Visible 期间也要跑：安全阀（15 秒）与重新贴边都挂在这个 tick 上
        bool shouldRun = _machine.State is HudState.Armed or HudState.Visible or HudState.Flashing;
        if (shouldRun == _timer.Enabled)
        {
            return;
        }

        if (shouldRun)
        {
            _timer.Start();
        }
        else
        {
            _timer.Stop();
        }
    }

    /// <summary>
    /// S1 调试块的固定几何（420×180）。**不是**真实面板的尺寸——真实面板由
    /// <see cref="HudPanel.Measure"/> 按内容算。这里保留硬编码是因为 S1 自检要一个
    /// 可预期的参照物来验证「窗口几何 = 请求的几何」。
    /// </summary>
    public static Rectangle DebugPanelBounds()
    {
        Rectangle area = Screen.PrimaryScreen?.Bounds ?? new Rectangle(0, 0, 1920, 1080);
        return new Rectangle(
            area.X + (area.Width - 420) / 2,
            area.Y + (area.Height - 180) / 2,
            420,
            180);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        _timer.Stop();
        _timer.Tick -= OnTimerTick;
        _timer.Dispose();

        // 退出必须卸钩，否则钩子会跟着残留到进程死亡
        _pageHook?.Dispose();
        _pageHook = null;

        _suppressTimer.Stop();
        _suppressTimer.Tick -= OnSuppressTick;
        _suppressTimer.Dispose();

        _input.Input -= OnInput;
        _machine.ShowRequested -= OnShowRequested;
        _machine.ComboFlashed -= OnComboFlashed;
        _machine.HideRequested -= OnHideRequested;
        _watcher.ForegroundChanged -= OnForegroundChanged;

        _watcher.Dispose();
        _input.Dispose();
        _overlay.Dispose();
    }
}
