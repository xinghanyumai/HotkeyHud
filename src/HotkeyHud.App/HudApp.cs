using System.Diagnostics;
using HotkeyHud.App.Platform;
using HotkeyHud.App.UI;
using HotkeyHud.Core;

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
    private readonly KnownShortcutFilter _known = new();
    private readonly ForegroundProbe _probe = new();
    private readonly ForegroundWatcher _watcher;
    private readonly SuppressionProbe _suppressionProbe = new();
    private readonly TriggerStateMachine _machine;
    private readonly System.Windows.Forms.Timer _timer;
    private readonly System.Windows.Forms.Timer _suppressTimer;

    private HudModel? _currentModel;
    private bool _manualPause;
    private bool _gameSuppressed;
    private bool _disposed;

    public HudApp(string? appsDirectory = null, GameRules? gameRules = null)
    {
        string directory = appsDirectory
                           ?? Path.Combine(AppContext.BaseDirectory, "Data", "apps");

        GameRules = gameRules ?? new GameRules();

        _repository = new ShortcutRepository(directory);
        // 先建会抛异常的那个，避免它失败时已经占着 Raw Input 窗口与 HGlobal
        _watcher = new ForegroundWatcher();
        _input = new RawInputSource();
        _overlay = new OverlayForm();
        _machine = new TriggerStateMachine { FlashPredicate = _known.AllowFlash };
        _timer = new System.Windows.Forms.Timer { Interval = TimerIntervalMs };
        _suppressTimer = new System.Windows.Forms.Timer { Interval = SuppressPollIntervalMs };

        _input.Input += OnInput;
        _watcher.ForegroundChanged += OnForegroundChanged;
        _machine.ShowRequested += OnShowRequested;
        _machine.ComboFlashed += OnComboFlashed;
        _machine.HideRequested += OnHideRequested;
        _timer.Tick += OnTimerTick;
        _suppressTimer.Tick += OnSuppressTick;

        RefreshSuppression();
        _suppressTimer.Start();
    }

    public RawInputSource Input => _input;

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

    /// <summary>最近一次抑制判定的命中原因，空串表示未抑制。</summary>
    public string SuppressReason { get; private set; } = string.Empty;

    /// <summary>最近一次读到的系统通知状态原值（2/3/4 会触发 D1），仅用于诊断。</summary>
    public int NotificationState => _suppressionProbe.LastNotificationState;

    public GameRules GameRules { get; }

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
            _machine.OnKey(new KeyEvent(evt.Code, evt.IsDown, evt.TimestampMs));
        }
        else
        {
            _machine.OnMouseButton();
        }

        SyncTimer();
    }

    private void OnForegroundChanged()
    {
        _probe.Invalidate();
        _suppressionProbe.Invalidate();
        _machine.OnForegroundChanged();
        SyncTimer();
        RefreshSuppression();
    }

    private void OnSuppressTick(object? sender, EventArgs e) => RefreshSuppression();

    /// <summary>
    /// 重新采集并判定是否抑制。只在「前台变化」与「2 秒轮询」两个时机调用——
    /// 绝不在 ShowRequested 里做，那是按键路径，必须零 I/O（§2.2）。
    /// </summary>
    private void RefreshSuppression()
    {
        try
        {
            GameSignal signal = _suppressionProbe.Capture();
            _gameSuppressed = GameClassifier.ShouldSuppress(signal, GameRules, out string reason);
            SuppressReason = reason;
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
            return;
        }

        // 仓库在启动时已 Reload，这里只查内存，不做 I/O（§六 禁止 3）
        string processName = _probe.ProcessName;
        string displayName = processName.Length > 0 ? processName : "未知进程";

        _currentModel = HudBuilder.Build(
            _repository.FindByProcess(processName), modifier, _known, displayName);

        RenderModel(_currentModel, null);
    }

    private void OnComboFlashed(Combo combo)
    {
        if (_machine.Suppressed || _currentModel is null)
        {
            return;
        }

        // 高亮只换颜色与背景，尺寸仍由同一份模型决定，不会重建 DIB
        RenderModel(_currentModel, combo);
    }

    private void OnHideRequested()
    {
        _overlay.HideOverlay();
        IsOverlayVisible = false;
        HighlightedCombo = null;
    }

    private void RenderModel(HudModel model, Combo? highlight)
    {
        _overlay.ShowModel(model, highlight);
        IsOverlayVisible = true;
        HighlightedCombo = highlight;

        CurrentLabel = model.HasProfile
            ? $"{model.AppName} · {model.TotalCount} 条 {model.Modifier} 快捷键"
            : $"未收录 {model.AppName}";
    }

    /// <summary>§3.2 计时器按需启停：只有 Armed / Flashing 需要 tick，Idle 必须停。</summary>
    private void SyncTimer()
    {
        bool shouldRun = _machine.State is HudState.Armed or HudState.Flashing;
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
