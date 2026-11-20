using System.Diagnostics;
using HotkeyHud.App.Shell.Adapters;

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
    private SettingsForm? _settings;
    private bool _disposed;

    public ShellController(string? configDirectory = null, string? appsDirectory = null)
    {
        _store = new ConfigStore(configDirectory);
        _config = _store.Load().With(AutoStart.IsEnabled());

        _hud = new HudApp(
            appsDirectory,
            config: _config,
            userAppsDirectory: Path.Combine(_store.DirectoryPath, "apps"));
        _tray = new TrayIcon(
            TogglePause, AddAppShortcuts, ShowSettings, Reload, OpenConfigDirectory, SetAutoStart, RequestExit);
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
        else if (!_config.FirstRunTipShown)
        {
            // 首次运行引导：只弹一次，用配置里的标记位（删了配置也不会再弹第二次）
            _tray.ShowBalloon(
                "HotkeyHud 已在托盘运行",
                "按住 Ctrl 或 Alt 查看当前应用的快捷键。托盘图标可能被折叠到「隐藏的图标」里。");
            _config = _config.WithFirstRunTipShown();
            _store.Save(_config);
        }
    }

    public event Action? ExitRequested;

    public HudApp Hud => _hud;

    public AppConfig Config => _config;

    /// <summary>托盘（验收用：气泡数、菜单项）。</summary>
    public TrayIcon TrayIconForTest => _tray;

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

    /// <summary>
    /// 托盘「设置…」：非模态单窗体，关闭即 Dispose，不常驻。
    /// 同一时刻只开一个——再点就把已有窗口拉到前面。
    /// </summary>
    public void ShowSettings()
    {
        if (_settings is { IsDisposed: false } existing)
        {
            existing.Activate();
            return;
        }

        var form = new SettingsForm(_hud.Repository, Reload);
        form.AddAppRequested += () =>
        {
            AddAppShortcuts();
            form.ReloadApps();
        };

        _settings = form;
        form.Show();
    }

    /// <summary>
    /// 托盘「添加应用快捷键…」（§3.4.3）：选 exe → 试适配器 → 命中就写用户档案，未命中就生成模板。
    /// 进程名一律从选中的 exe 取，不让用户手写。
    /// </summary>
    public void AddAppShortcuts()
    {
        using var dialog = new OpenFileDialog
        {
            Title = "选择要添加快捷键的应用",
            Filter = "应用程序 (*.exe)|*.exe",
            CheckFileExists = true,
        };

        if (dialog.ShowDialog() != DialogResult.OK)
        {
            return;
        }

        string exe = dialog.FileName;
        string directory = Path.Combine(_store.DirectoryPath, "apps");
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

        try
        {
            if (VsCodeAdapter.Matches(exe))
            {
                VsCodeAdapter.TryRead(exe, appData, out List<AdapterEntry> entries, out string reason);
                SaveAdapterResult(directory, exe, entries, VsCodeAdapter.GroupName, "VS Code", reason);
                return;
            }

            if (JetBrainsAdapter.Matches(exe))
            {
                JetBrainsAdapter.TryRead(
                    exe, appData, out List<AdapterEntry> entries, out string reason, out string product);
                string label = product.Length > 0 ? $"JetBrains（{product}）" : "JetBrains";
                SaveAdapterResult(directory, exe, entries, JetBrainsAdapter.GroupName, label, reason);
                return;
            }

            // 没有可读来源：生成模板并打开，明确告诉用户「未匹配」
            string path = UserApps.WriteTemplate(directory, exe);
            OpenInEditor(path);
            _tray.ShowBalloon(
                "未匹配到自动读取",
                $"{Path.GetFileName(exe)} 没有可读的快捷键配置，已生成模板：\n{Path.GetFileName(path)}\n"
                + "填好后点「重载数据」。");
        }
        catch (Exception ex)
        {
            _tray.ShowBalloon("添加失败", ex.Message, warning: true);
        }
        finally
        {
            Reload();
        }
    }

    private void SaveAdapterResult(
        string directory,
        string exe,
        List<AdapterEntry> entries,
        string group,
        string label,
        string reason)
    {
        string path = entries.Count > 0
            ? UserApps.WriteEntries(directory, exe, entries, group)
            : UserApps.WriteEntries(directory, exe, Array.Empty<AdapterEntry>(), group);

        if (entries.Count > 0)
        {
            _tray.ShowBalloon("已添加", $"从 {label} 读取 {entries.Count} 条自定义键位。");
            return;
        }

        // 读到文件但 0 条：这是正确行为（用户没改过键位），不是失败
        OpenInEditor(path);
        _tray.ShowBalloon(
            "没有自定义键位",
            $"{label}：{reason}\n已建空档案，改过键位后重载即可。",
            warning: true);
    }

    private static void OpenInEditor(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            // 打不开编辑器不算失败：文件已经写好了，用户可以自己去配置目录打开
            Debug.WriteLine($"[HotkeyHud] 打开 {path} 失败：{ex.Message}");
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

    private void RequestExit()
    {
        // 退出前必须让用户知道「开机自启」还开着——否则下次开机它又起来了，用户以为是 bug
        if (_config.StartWithWindows)
        {
            DialogResult answer = MessageBox.Show(
                "开机自启仍开启，退出后下次开机还会自动启动。\n\n要同时关闭开机自启吗？",
                "HotkeyHud",
                MessageBoxButtons.YesNoCancel,
                MessageBoxIcon.Question);

            if (answer == DialogResult.Cancel)
            {
                return;
            }

            if (answer == DialogResult.Yes)
            {
                SetAutoStart(false);
            }
        }

        ExitRequested?.Invoke();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _tray.RefreshRequested -= PushTrayState;
        _settings?.Dispose();
        _settings = null;
        _tray.Dispose();
        _hud.Dispose();
    }
}
