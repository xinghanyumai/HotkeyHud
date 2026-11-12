using System.Diagnostics;

namespace HotkeyHud.App.Shell;

/// <summary>
/// 外壳编排：配置 → HudApp → 托盘。托盘只调这里的方法，不直接碰 HudApp 的内部状态。
/// <para>
/// **抑制只有一个赋值点**：<see cref="HudApp.Suppressed"/>（手动暂停）与游戏抑制在
/// <c>HudApp.ApplySuppression</c> 里取并集。这里不许出现第二个 <c>machine.Suppressed = ...</c>。
/// </para>
/// </summary>
public sealed class ShellController : IDisposable
{
    private readonly ConfigStore _store;
    private readonly HudApp _hud;
    private readonly TrayIcon _tray;

    private AppConfig _config;
    private bool _disposed;

    public ShellController(string? configDirectory = null, string? appsDirectory = null)
    {
        _store = new ConfigStore(configDirectory);
        _config = _store.Load().With(AutoStart.IsEnabled());

        _hud = new HudApp(appsDirectory, config: _config);
        _tray = new TrayIcon(TogglePause, Reload, OpenConfigDirectory, SetAutoStart, RequestExit);
        _tray.RefreshRequested += PushTrayState;

        PushTrayState();

        if (_store.LastLoadWasCorrupt)
        {
            _tray.ShowBalloon(
                "配置已重置",
                $"config.json 无法解析（{_store.LastCorruptReason}），已改名 config.broken.json，"
                + "本次用默认值启动。",
                warning: true);
        }
        else if (_store.LastRepairs.Count > 0)
        {
            // 钳制必须说出来，否则用户会以为改生效了
            _tray.ShowBalloon("配置已修正", string.Join("；", _store.LastRepairs), warning: true);
        }
    }

    public event Action? ExitRequested;

    public HudApp Hud => _hud;

    public AppConfig Config => _config;

    public ConfigStore Store => _store;

    public TrayIcon Tray => _tray;

    /// <summary>托盘「暂停 / 恢复」。只改手动暂停这一路，不影响游戏抑制。</summary>
    public void TogglePause()
    {
        _hud.Suppressed = !_hud.Suppressed;
        PushTrayState();
    }

    /// <summary>重载配置与档案。配置里改过的 dwellMs / knownShortcuts 在这里真正生效。</summary>
    public void Reload()
    {
        _config = _store.Load().With(AutoStart.IsEnabled());
        _hud.ReloadData(_config);
        PushTrayState();

        if (_store.LastRepairs.Count > 0)
        {
            _tray.ShowBalloon("配置已修正", string.Join("；", _store.LastRepairs), warning: true);
        }
    }

    public void OpenConfigDirectory()
    {
        try
        {
            Directory.CreateDirectory(_store.DirectoryPath);
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{_store.DirectoryPath}\"",
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            _tray.ShowBalloon("打不开配置目录", ex.Message, warning: true);
        }
    }

    public void SetAutoStart(bool enabled)
    {
        try
        {
            AutoStart.Set(enabled);
            _config = _config.With(enabled);
            _store.Save(_config);
        }
        catch (Exception ex)
        {
            _tray.ShowBalloon("开机自启设置失败", ex.Message, warning: true);
        }

        PushTrayState();
    }

    public void PushTrayState()
    {
        _tray.Update(
            paused: _hud.Suppressed,
            suppressed: _hud.Suppressed || _hud.GameSuppressed,
            reason: _hud.GameSuppressed ? _hud.SuppressReason : "手动暂停",
            foreground: string.IsNullOrEmpty(_hud.ForegroundApp) ? "未知进程" : _hud.ForegroundApp,
            autoStart: AutoStart.IsEnabled());
    }

    private void RequestExit() => ExitRequested?.Invoke();

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _tray.RefreshRequested -= PushTrayState;
        _tray.Dispose();
        _hud.Dispose();
    }
}
