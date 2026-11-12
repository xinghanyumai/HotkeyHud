using HotkeyHud.App.Platform;
using HotkeyHud.App.UI;

namespace HotkeyHud.App;

/// <summary>
/// S1 编排：只做「采集输入 → 可显隐覆盖窗」，不含任何快捷键判定与状态机。
/// F9 仅用于本阶段手工验证（S2 接入状态机后移除）。
/// </summary>
public sealed class HudApp : IDisposable
{
    private const int ToggleKey = 0x78; // VK_F9

    private readonly RawInputSource _input;
    private readonly OverlayForm _overlay;
    private bool _visible;
    private bool _disposed;

    public HudApp()
    {
        _input = new RawInputSource();
        _overlay = new OverlayForm();
        _input.Input += OnInput;
    }

    public RawInputSource Input => _input;

    public OverlayForm Overlay => _overlay;

    public bool IsOverlayVisible => _visible;

    private void OnInput(InputEvent evt)
    {
        if (evt.Kind == InputKind.Key && evt.Code == ToggleKey && evt.IsDown)
        {
            Toggle();
        }
    }

    public void Toggle()
    {
        if (_visible)
        {
            HideOverlay();
        }
        else
        {
            ShowOverlay();
        }
    }

    public void ShowOverlay()
    {
        Rectangle area = Screen.PrimaryScreen?.Bounds ?? new Rectangle(0, 0, 1920, 1080);
        var bounds = new Rectangle(
            area.X + (area.Width - 420) / 2,
            area.Y + (area.Height - 180) / 2,
            420,
            180);

        _overlay.ShowNoActivate(bounds, Color.FromArgb(235, 24, 24, 28), "HUD · S1");
        _visible = true;
    }

    public void HideOverlay()
    {
        _overlay.HideOverlay();
        _visible = false;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _input.Input -= OnInput;
        _input.Dispose();
        _overlay.Dispose();
    }
}
