namespace HotkeyHud.Core;

public enum HudState
{
    Idle,
    Armed,
    Visible,
    Flashing,
}

/// <summary>
/// §5 状态机。时间只有两个来源：<see cref="OnTick"/> 的 nowMs 与 <see cref="KeyEvent.TimestampMs"/>。
/// 内部不持有 Stopwatch / DateTime / Timer，否则时序规则无法可靠验证。
/// <para>
/// 契约要求：两个时间必须同源（同一个单调时钟）。混用会直接让驻留时长判定失真。
/// </para>
/// </summary>
public sealed class TriggerStateMachine
{
    /// <summary>§5.3.2 规定的阈值下界：低于 200ms 正常打字会闪，80ms 是硬下限。</summary>
    public const int MinTimingMs = 80;

    /// <summary>阈值上界：高于 400ms 查询意图会落空，5000ms 是硬上限。</summary>
    public const int MaxTimingMs = 5000;

    private readonly HashSet<int> _heldNonModifierKeys = new();

    private Modifier _heldModifiers;
    private int _dwellMs = 300;
    private int _flashMs = 500;
    private long _armedAtMs;
    private long _flashStartedMs;
    private bool _blockedUntilRelease;
    private bool _suppressed;

    public TriggerStateMachine(int dwellMs = 300, int flashMs = 500)
    {
        DwellMs = dwellMs;
        FlashMs = flashMs;
    }

    /// <summary>长按阈值。越界抛 <see cref="ArgumentOutOfRangeException"/>，不静默接受。</summary>
    public int DwellMs
    {
        get => _dwellMs;
        set => _dwellMs = ValidateTiming(value, nameof(DwellMs));
    }

    /// <summary>闪烁时长。越界抛 <see cref="ArgumentOutOfRangeException"/>，不静默接受。</summary>
    public int FlashMs
    {
        get => _flashMs;
        set => _flashMs = ValidateTiming(value, nameof(FlashMs));
    }

    public HudState State { get; private set; } = HudState.Idle;

    public Modifier TriggerModifier { get; private set; } = Modifier.None;

    public Combo? FlashedCombo { get; private set; }

    /// <summary>置 true 立即收起；为 true 期间不接受新的长按。</summary>
    public bool Suppressed
    {
        get => _suppressed;
        set
        {
            if (_suppressed == value)
            {
                return;
            }

            _suppressed = value;
            if (value)
            {
                Collapse(raiseHide: true, block: false);
            }
        }
    }

    /// <summary>返回 true 表示该组合需要高亮。null = 全部高亮（未接熟记名单时的行为）。</summary>
    public Func<Combo, bool>? FlashPredicate { get; set; }

    public event Action<Modifier>? ShowRequested;

    public event Action? HideRequested;

    public event Action<Combo>? ComboFlashed;

    public void OnKey(in KeyEvent e)
    {
        Modifier modifier = Keys.ModifierOf(e.VirtualKey);

        if (modifier != Modifier.None)
        {
            if (e.IsDown)
            {
                HandleModifierDown(modifier, e.TimestampMs);
            }
            else
            {
                HandleModifierUp(modifier);
            }

            return;
        }

        if (e.IsDown)
        {
            _heldNonModifierKeys.Add(e.VirtualKey);
            HandleNonModifierDown(e.VirtualKey, e.TimestampMs);
        }
        else
        {
            _heldNonModifierKeys.Remove(e.VirtualKey);
            TryUnblock();
        }
    }

    public void OnTick(long nowMs)
    {
        switch (State)
        {
            case HudState.Armed:
                if (nowMs - _armedAtMs >= DwellMs)
                {
                    State = HudState.Visible;
                    ShowRequested?.Invoke(TriggerModifier);
                }

                return;

            case HudState.Flashing:
                if (nowMs - _flashStartedMs >= FlashMs)
                {
                    Collapse(raiseHide: true, block: true);
                }

                return;
        }
    }

    public void OnMouseButton() => CollapseIfActive(block: false);

    public void OnForegroundChanged()
    {
        // 前台变了，物理键状态本来就不可知：修饰键与非修饰键一律视为已松开。
        // 否则系统真丢一次 Ctrl 的 key-up，blockedUntilRelease 永不解除，HUD 会彻底失效且无提示。
        _heldModifiers = Modifier.None;
        _heldNonModifierKeys.Clear();
        CollapseIfActive(block: false);
        TryUnblock();
    }

    /// <summary>回到初始状态。面板正在显示时会先发一次收起，避免 UI 残留。</summary>
    public void Reset()
    {
        bool wasShowing = State is HudState.Visible or HudState.Flashing;

        State = HudState.Idle;
        TriggerModifier = Modifier.None;
        FlashedCombo = null;
        _heldModifiers = Modifier.None;
        _heldNonModifierKeys.Clear();
        _blockedUntilRelease = false;

        if (wasShowing)
        {
            HideRequested?.Invoke();
        }
    }

    private void HandleModifierDown(Modifier modifier, long timestampMs)
    {
        if ((_heldModifiers & modifier) != 0)
        {
            // 自动重复：同一个修饰键仍按住时系统会持续发 down。
            // 不挡住的话，Visible 期间按住的 Ctrl 会把刚弹出的面板打掉（§5.3.1 的同一类坑）。
            return;
        }

        bool otherModifierHeld = (_heldModifiers & ~modifier) != Modifier.None;
        _heldModifiers |= modifier;

        switch (State)
        {
            case HudState.Idle:
                // 已有其他修饰键 / 阻塞中 / 抑制中 → 忽略
                if (otherModifierHeld || _blockedUntilRelease || _suppressed)
                {
                    return;
                }

                TriggerModifier = modifier;
                _armedAtMs = timestampMs;
                State = HudState.Armed;
                return;

            case HudState.Armed:
                // 同一修饰键的重复已在入口挡掉，走到这里必然是「另一个修饰键」
                Collapse(raiseHide: false, block: false);
                return;

            case HudState.Visible:
            case HudState.Flashing:
                // §5.2 只写了 Visible；Flashing 按同样规则处理（见 [SPEC-GAP] 1）
                Collapse(raiseHide: true, block: true);
                return;
        }
    }

    private void HandleModifierUp(Modifier modifier)
    {
        _heldModifiers &= ~modifier;

        if (modifier == TriggerModifier)
        {
            if (State == HudState.Armed)
            {
                Collapse(raiseHide: false, block: false);
            }
            else if (State is HudState.Visible or HudState.Flashing)
            {
                Collapse(raiseHide: true, block: false);
            }
        }

        TryUnblock();
    }

    private void HandleNonModifierDown(int virtualKey, long timestampMs)
    {
        switch (State)
        {
            case HudState.Idle:
                return;

            case HudState.Armed:
                // 具体组合按下 → 取消，并阻塞到全部松开
                Collapse(raiseHide: false, block: true);
                return;

            case HudState.Visible:
            case HudState.Flashing:
                var combo = new Combo(_heldModifiers, virtualKey);
                if (ShouldFlash(combo))
                {
                    State = HudState.Flashing;
                    FlashedCombo = combo;
                    _flashStartedMs = timestampMs;
                    ComboFlashed?.Invoke(combo);
                }
                else
                {
                    // 已牢记：立即收起，不进入 Flashing（§4.3 第二个作用点）
                    Collapse(raiseHide: true, block: true);
                }

                return;
        }
    }

    private bool ShouldFlash(Combo combo) => FlashPredicate?.Invoke(combo) ?? true;

    private static int ValidateTiming(int value, string name)
        => value is >= MinTimingMs and <= MaxTimingMs
            ? value
            : throw new ArgumentOutOfRangeException(
                name, value, $"{name} 必须在 {MinTimingMs}–{MaxTimingMs} ms 之间");

    /// <summary>§5.3.1：必须等修饰键与非修饰键全部松开，否则 Ctrl+C 之后会突然弹面板。</summary>
    private void TryUnblock()
    {
        if (_heldModifiers == Modifier.None && _heldNonModifierKeys.Count == 0)
        {
            _blockedUntilRelease = false;
        }
    }

    private void CollapseIfActive(bool block)
    {
        if (State == HudState.Idle)
        {
            return;
        }

        Collapse(raiseHide: true, block: block);
    }

    private void Collapse(bool raiseHide, bool block)
    {
        bool wasShowing = State is HudState.Visible or HudState.Flashing;

        State = HudState.Idle;
        TriggerModifier = Modifier.None;
        FlashedCombo = null;

        if (block)
        {
            _blockedUntilRelease = true;
        }

        if (raiseHide && wasShowing)
        {
            HideRequested?.Invoke();
        }
    }
}
