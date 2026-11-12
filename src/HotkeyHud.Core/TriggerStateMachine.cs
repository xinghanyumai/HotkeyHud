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
/// </summary>
public sealed class TriggerStateMachine
{
    private Modifier _heldModifiers;
    private long _armedAtMs;
    private long _flashStartedMs;
    private bool _blockedUntilRelease;
    private bool _suppressed;

    public TriggerStateMachine(int dwellMs = 300, int flashMs = 500)
    {
        DwellMs = dwellMs;
        FlashMs = flashMs;
    }

    public int DwellMs { get; set; }

    public int FlashMs { get; set; }

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

        // 非修饰键松开不影响任何状态
        if (e.IsDown)
        {
            HandleNonModifierDown(e.VirtualKey, e.TimestampMs);
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

    public void OnForegroundChanged() => CollapseIfActive(block: false);

    public void Reset()
    {
        State = HudState.Idle;
        TriggerModifier = Modifier.None;
        FlashedCombo = null;
        _heldModifiers = Modifier.None;
        _blockedUntilRelease = false;
    }

    private void HandleModifierDown(Modifier modifier, long timestampMs)
    {
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
                // 同一修饰键的自动重复不算「另一个修饰键」
                if (modifier != TriggerModifier)
                {
                    Collapse(raiseHide: false, block: false);
                }

                return;

            case HudState.Visible:
            case HudState.Flashing:
                // §5.2 只写了 Visible；Flashing 按同样规则处理（见 [SPEC-GAP]）
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

        // 全部松开才解除阻塞，否则 Ctrl+C 之后按住 Ctrl 会再次弹面板
        if (_heldModifiers == Modifier.None)
        {
            _blockedUntilRelease = false;
        }
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
