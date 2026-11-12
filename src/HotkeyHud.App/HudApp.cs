using HotkeyHud.App.Platform;
using HotkeyHud.App.UI;
using HotkeyHud.Core;
// WinForms 也导出了 Keys，这里显式指向 Core 的键位工具
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
    private const int OverlayAlpha = 235;

    private readonly RawInputSource _input;
    private readonly OverlayForm _overlay;
    private readonly ShortcutRepository _repository;
    private readonly KnownShortcutFilter _known = new();
    private readonly ForegroundProbe _probe = new();
    private readonly ForegroundWatcher _watcher;
    private readonly TriggerStateMachine _machine;
    private readonly System.Windows.Forms.Timer _timer;

    private HudModel? _currentModel;
    private bool _disposed;

    public HudApp(string? appsDirectory = null)
    {
        string directory = appsDirectory
                           ?? Path.Combine(AppContext.BaseDirectory, "Data", "apps");

        _repository = new ShortcutRepository(directory);
        _input = new RawInputSource();
        _overlay = new OverlayForm();
        _watcher = new ForegroundWatcher();
        _machine = new TriggerStateMachine { FlashPredicate = _known.AllowFlash };
        _timer = new System.Windows.Forms.Timer { Interval = TimerIntervalMs };

        _input.Input += OnInput;
        _watcher.ForegroundChanged += OnForegroundChanged;
        _machine.ShowRequested += OnShowRequested;
        _machine.ComboFlashed += OnComboFlashed;
        _machine.HideRequested += OnHideRequested;
        _timer.Tick += OnTimerTick;
    }

    public RawInputSource Input => _input;

    public OverlayForm Overlay => _overlay;

    public bool IsOverlayVisible { get; private set; }

    /// <summary>面板当前显示的文字，供集成测试与调试观察。</summary>
    public string CurrentLabel { get; private set; } = string.Empty;

    /// <summary>计时器是否在跑。空闲时必须为 false（§八 空载 CPU 要求）。</summary>
    public bool TimerRunning => _timer.Enabled;

    /// <summary>状态机当前状态，供集成测试诊断用。</summary>
    public HudState State => _machine.State;

    /// <summary>手动暂停（§1 的 G1）。S2.5 只有这一个抑制来源，游戏抑制是 S4。</summary>
    public bool Suppressed
    {
        get => _machine.Suppressed;
        set
        {
            _machine.Suppressed = value;
            SyncTimer();
        }
    }

    public void ShowOverlay()
    {
        if (_machine.Suppressed)
        {
            return;
        }

        Render(Modifier.Ctrl, BuildLabel(Modifier.Ctrl));
    }

    public void HideOverlay()
    {
        _overlay.HideOverlay();
        IsOverlayVisible = false;
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
        _machine.OnForegroundChanged();
        SyncTimer();
    }

    private void OnTimerTick(object? sender, EventArgs e)
    {
        _machine.OnTick(_input.NowMs);
        SyncTimer();
    }

    private void OnShowRequested(Modifier modifier)
    {
        if (_machine.Suppressed)
        {
            return;
        }

        Render(modifier, BuildLabel(modifier));
    }

    private void OnComboFlashed(Combo combo)
    {
        if (_machine.Suppressed)
        {
            return;
        }

        string keys = KeyNames.Format(combo);
        string action = FindAction(keys);
        Render(_machine.TriggerModifier, action.Length > 0 ? $"{keys} · {action}" : keys);
    }

    private void OnHideRequested()
    {
        _overlay.HideOverlay();
        IsOverlayVisible = false;
    }

    private string BuildLabel(Modifier modifier)
    {
        string processName = _probe.ProcessName;
        _currentModel = HudBuilder.Build(
            _repository.FindByProcess(processName), modifier, _known);

        return _currentModel.HasProfile
            ? $"{_currentModel.AppName} · {_currentModel.TotalCount} 条 {modifier} 快捷键"
            : $"未收录 {processName}";
    }

    private string FindAction(string keys)
    {
        if (_currentModel is null)
        {
            return string.Empty;
        }

        foreach (HudGroup group in _currentModel.Groups)
        {
            foreach (HudItem item in group.Items)
            {
                if (string.Equals(item.Keys, keys, StringComparison.Ordinal))
                {
                    return item.Action;
                }
            }
        }

        return string.Empty;
    }

    private void Render(Modifier modifier, string label)
    {
        _overlay.ShowNoActivate(PanelBounds(), ColorFor(modifier), label);
        IsOverlayVisible = true;
        CurrentLabel = label;
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

    /// <summary>面板几何的唯一来源：显示与自检都用它，避免自检和实现各写一份坐标。</summary>
    public static Rectangle PanelBounds()
    {
        Rectangle area = Screen.PrimaryScreen?.Bounds ?? new Rectangle(0, 0, 1920, 1080);
        return new Rectangle(
            area.X + (area.Width - 420) / 2,
            area.Y + (area.Height - 180) / 2,
            420,
            180);
    }

    /// <summary>规格 §6 的强调色：Ctrl 蓝 / Alt 橙 / Shift 绿 / Win 青。</summary>
    private static Color ColorFor(Modifier modifier) => modifier switch
    {
        Modifier.Alt => Color.FromArgb(OverlayAlpha, 0xFF, 0x9F, 0x45),
        Modifier.Shift => Color.FromArgb(OverlayAlpha, 0x3D, 0xDC, 0x97),
        Modifier.Win => Color.FromArgb(OverlayAlpha, 0x4C, 0xC9, 0xF0),
        _ => Color.FromArgb(OverlayAlpha, 0x4C, 0x8D, 0xFF),
    };

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
