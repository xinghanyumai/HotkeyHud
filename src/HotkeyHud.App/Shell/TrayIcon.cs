using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace HotkeyHud.App.Shell;

/// <summary>
/// 托盘图标与菜单。图标用代码画：16×16，Ctrl 强调色底 + 白色键帽形。
/// 不用 <c>SystemIcons.Application</c>——那和几百个程序长得一样。
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private readonly NotifyIcon _notifyIcon;
    private readonly Icon _icon;
    private readonly ContextMenuStrip _menu;
    private readonly ToolStripMenuItem _statusItem;
    private readonly ToolStripMenuItem _pauseItem;
    private readonly ToolStripMenuItem _autoStartItem;

    private bool _disposed;

    public TrayIcon(
        Action togglePause,
        Action addAppShortcuts,
        Action openSettings,
        Action reload,
        Action openConfigDirectory,
        Action<bool> setAutoStart,
        Action exit)
    {
        _icon = LoadAppIcon() ?? CreateIcon();

        _statusItem = new ToolStripMenuItem("正常") { Enabled = false };
        _pauseItem = new ToolStripMenuItem("暂停", null, (_, _) => togglePause());
        _autoStartItem = new ToolStripMenuItem("开机自启")
        {
            CheckOnClick = false,
        };
        _autoStartItem.Click += (_, _) => setAutoStart(!_autoStartItem.Checked);

        _menu = new ContextMenuStrip();
        _menu.Items.Add(_statusItem);
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(_pauseItem);
        _menu.Items.Add(new ToolStripMenuItem("添加应用快捷键…", null, (_, _) => addAppShortcuts()));
        _menu.Items.Add(new ToolStripMenuItem("重载数据", null, (_, _) => reload()));
        _menu.Items.Add(new ToolStripMenuItem("打开配置目录", null, (_, _) => openConfigDirectory()));
        _menu.Items.Add(_autoStartItem);
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(new ToolStripMenuItem("设置…", null, (_, _) => openSettings()));
        _menu.Items.Add(new ToolStripMenuItem("退出", null, (_, _) => exit()));
        _menu.Opening += (_, _) => RefreshRequested?.Invoke();

        _notifyIcon = new NotifyIcon
        {
            Icon = _icon,
            Text = "HotkeyHud",
            Visible = true,
            ContextMenuStrip = _menu,
        };
    }

    /// <summary>菜单即将弹出时触发，让外层把最新状态推过来。</summary>
    public event Action? RefreshRequested;

    /// <summary>菜单项数量（含禁用项与分隔线），验收用。</summary>
    public int MenuItemCount => _menu.Items.Count;

    /// <summary>可点击的菜单项文本，验收用。</summary>
    public IReadOnlyList<string> ActionItems => _menu.Items
        .OfType<ToolStripMenuItem>()
        .Where(item => !ReferenceEquals(item, _statusItem))
        .Select(item => item.Text ?? string.Empty)
        .ToArray();

    /// <summary>状态行文本，验收用。</summary>
    public string StatusText => _statusItem.Text ?? string.Empty;

    /// <summary>暂停项当前文案（「暂停」/「恢复」），验收用。</summary>
    public string PauseItemText => _pauseItem.Text ?? string.Empty;

    public void Update(bool paused, bool suppressed, string reason, string foreground, bool autoStart)
    {
        string state = suppressed
            ? $"已抑制（{(paused ? "手动暂停" : reason)}）"
            : "正常";
        _statusItem.Text = $"{state} · 前台 {foreground}";
        _pauseItem.Text = paused ? "恢复" : "暂停";
        _autoStartItem.Checked = autoStart;
        _notifyIcon.Text = $"HotkeyHud · {state}";
    }

    public void ShowBalloon(string title, string text, bool warning = false)
    {
        _notifyIcon.BalloonTipTitle = title;
        _notifyIcon.BalloonTipText = text;
        _notifyIcon.BalloonTipIcon = warning ? ToolTipIcon.Warning : ToolTipIcon.Info;
        _notifyIcon.ShowBalloonTip(4000);
    }

    /// <summary>优先用打包的 app.ico（六档、与 exe 图标一致），取不到再退回代码绘制。</summary>
    private static Icon? LoadAppIcon()
    {
        try
        {
            string path = Path.Combine(AppContext.BaseDirectory, "app.ico");
            return File.Exists(path) ? new Icon(path, 16, 16) : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static Icon CreateIcon()
    {
        using var bitmap = new Bitmap(16, 16, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.Clear(Color.Transparent);

            using (var background = new SolidBrush(Color.FromArgb(0x4C, 0x8D, 0xFF)))
            using (var backgroundPath = RoundedRect(new Rectangle(0, 0, 16, 16), 4))
            {
                graphics.FillPath(background, backgroundPath);
            }

            // 键帽：一个白色圆角矩形 + 底部一条细阴影，16px 下只能表达这么多
            using (var cap = new SolidBrush(Color.White))
            using (var capPath = RoundedRect(new Rectangle(3, 4, 10, 8), 2))
            {
                graphics.FillPath(cap, capPath);
            }
        }

        IntPtr handle = bitmap.GetHicon();
        try
        {
            // Clone 一份，脱离 handle 生命周期后再销毁 handle，避免 GDI 泄漏
            using Icon temporary = Icon.FromHandle(handle);
            return (Icon)temporary.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    private static GraphicsPath RoundedRect(Rectangle bounds, int radius)
    {
        int d = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(bounds.X, bounds.Y, d, d, 180, 90);
        path.AddArc(bounds.Right - d, bounds.Y, d, d, 270, 90);
        path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
        path.AddArc(bounds.X, bounds.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _menu.Dispose();
        _icon.Dispose();
    }
}
