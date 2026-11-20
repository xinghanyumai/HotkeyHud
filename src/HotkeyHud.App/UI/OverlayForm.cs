using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using HotkeyHud.App.Platform;
using HotkeyHud.Core;

namespace HotkeyHud.App.UI;

/// <summary>
/// 分层覆盖窗。三条硬性要求（坑 #2 / #9）：
/// 不抢焦点（WS_EX_NOACTIVATE + SW_SHOWNOACTIVATE）、鼠标穿透（WS_EX_TRANSPARENT）、
/// 不进 Alt+Tab 与任务栏（WS_EX_TOOLWINDOW）。
/// <para>窗口与合成在这里，面板长什么样在 <see cref="HudPanel"/>。</para>
/// </summary>
public sealed class OverlayForm : Form
{
    private IntPtr _dibSection;
    private IntPtr _dibBits;
    private IntPtr _memDc;
    private IntPtr _oldBitmap;
    private int _dibWidth;
    private int _dibHeight;

    private Bitmap? _canvas;
    private Graphics? _canvasGraphics;

    // 基图层缓存：同一份模型的连续高亮切换只重画高亮，不整帧重绘
    private Bitmap? _baseCanvas;
    private HudModel? _baseModel;
    private IReadOnlyList<HudPanel.PlacedItem>? _baseItems;
    private int _baseWidth;
    private float _baseScale;

    // 淡入（§6 v1.4：120ms、16ms 一帧、ease-out；隐藏不做动画）
    private const int FadeDurationMs = 120;
    private const int FadeFrameMs = 16;
    private const int FadeFrames = FadeDurationMs / FadeFrameMs;

    private readonly Func<bool> _animationsEnabled;
    private readonly System.Windows.Forms.Timer _fadeTimer;
    private int _fadeFrame;
    private bool _isVisible;
    private HudModel? _lastModel;
    private IntPtr _anchorMonitor;
    private Combo? _lastHighlight;
    private float _lastScale = 1f;

    public OverlayForm(Func<bool>? animationsEnabled = null)
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        SetStyle(ControlStyles.UserPaint | ControlStyles.Opaque, true);

        _animationsEnabled = animationsEnabled ?? SystemAnimationsEnabled;
        _fadeTimer = new System.Windows.Forms.Timer { Interval = FadeFrameMs };
        _fadeTimer.Tick += OnFadeTick;
    }

    /// <summary>本次显示一共推了几帧。1 = 没有动画；验收用它区分「有淡入」与「无淡入」。</summary>
    public int FadeFramesPushed { get; private set; }

    /// <summary>钩子吞掉 ↑/↓ 后投递的翻页消息。参数为 +1 / -1。</summary>
    public const int WmPageRequest = 0x8000 + 0x51;

    /// <summary>翻页请求。钩子只负责投递，真正的翻页由这里转给状态机。</summary>
    public event Action<int>? PageRequested;

    /// <summary>系统「减少动画」开关（SPI_GETCLIENTAREAANIMATION）。</summary>
    public static bool SystemAnimationsEnabled()
        => !NativeMethods.SystemParametersInfo(NativeMethods.SPI_GETCLIENTAREAANIMATION, 0, out bool enabled, 0)
           || enabled;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.Style = NativeMethods.WS_POPUP;
            cp.ExStyle = NativeMethods.WS_EX_LAYERED
                         | NativeMethods.WS_EX_TRANSPARENT
                         | NativeMethods.WS_EX_NOACTIVATE
                         | NativeMethods.WS_EX_TOOLWINDOW;
            return cp;
        }
    }

    /// <summary>
    /// 按 §6 规格渲染快捷键面板：先按光标挑屏，再用该屏的 DPI 算尺寸，最后决定贴下缘还是上缘。
    /// 每次呈现都重算——光标换屏或换半边时必须跟着走。
    /// </summary>
    /// <param name="reanchor">
    /// 是否重新算位置。翻页 / 高亮传 <c>false</c>：尺寸由格数预算固定，
    /// 位置沿用上一次——重算会因光标位置或边界钳制让面板跳一下。
    /// </param>
    public void ShowModel(HudModel model, Combo? highlight, bool reanchor = true)
    {
        if (!reanchor && _isVisible && LastBounds.Width > 0)
        {
            // 只重绘内容，不动窗口几何
            PresentModel(LastBounds, model, highlight, _lastScale);
            return;
        }

        (Rectangle bounds, float scale, IntPtr monitor) = ResolveBounds(model);
        _anchorMonitor = monitor;
        PresentModel(bounds, model, highlight, scale);
    }

    /// <summary>
    /// 面板已经显示时重新贴边。返回 true 表示位置变了。
    /// 由 20ms 计时器驱动，保证「光标换半边 → 100ms 内翻到另一侧」。
    /// </summary>
    public bool RefreshAnchor()
    {
        if (!_isVisible || _lastModel is null)
        {
            return false;
        }

        // 锚定在显示时定一次：同屏内移动光标不再上下跳，只有换屏才重锚
        (Rectangle bounds, float scale, IntPtr monitor) = ResolveBounds(_lastModel);
        if (monitor == _anchorMonitor)
        {
            return false;
        }

        _anchorMonitor = monitor;
        PresentModel(bounds, _lastModel, _lastHighlight, scale);
        return true;
    }

    private (Rectangle Bounds, float Scale, IntPtr Monitor) ResolveBounds(HudModel model)
    {
        Point cursor = Cursor.Position;
        MonitorInfo monitor = ScreenAnchor.Pick(EnumerateMonitors(), cursor);
        float scale = monitor.Scale;
        Size size = HudPanel.Measure(model, scale, monitor.WorkArea.Width);
        return (ScreenAnchor.Anchor(monitor, size, cursor), scale, monitor.Handle);
    }

    /// <summary>把 WinForms 的屏幕表补上每屏 DPI。GetDpiForMonitor 拿的是「该屏」的缩放，与窗口当前在哪无关。</summary>
    private static IReadOnlyList<MonitorInfo> EnumerateMonitors()
    {
        Screen[] screens = Screen.AllScreens;
        var monitors = new List<MonitorInfo>(screens.Length);

        foreach (Screen screen in screens)
        {
            var center = new NativeMethods.POINT(
                screen.Bounds.X + screen.Bounds.Width / 2,
                screen.Bounds.Y + screen.Bounds.Height / 2);

            uint dpi = 96;
            IntPtr handle = NativeMethods.MonitorFromPoint(center, NativeMethods.MONITOR_DEFAULTTONEAREST);
            if (handle != IntPtr.Zero
                && NativeMethods.GetDpiForMonitor(handle, NativeMethods.MDT_EFFECTIVE_DPI, out uint dpiX, out _) == 0
                && dpiX > 0)
            {
                dpi = dpiX;
            }

            monitors.Add(new MonitorInfo(handle, screen.Bounds, screen.WorkingArea, dpi));
        }

        return monitors;
    }

    /// <summary>S1 自检用的最简显示：一块纯色圆角 + 一行文字。不要删，--selftest 依赖它。</summary>
    public void ShowNoActivate(Rectangle bounds, Color background, string label)
    {
        Present(bounds, graphics => DrawDebugLabel(graphics, bounds.Width, bounds.Height, background, label));
    }

    /// <summary>隐藏必须立即生效——这是「不打扰」的底线，不做淡出。</summary>
    public void HideOverlay()
    {
        _fadeTimer.Stop();
        _isVisible = false;

        if (IsHandleCreated)
        {
            NativeMethods.ShowWindow(Handle, NativeMethods.SW_HIDE);
        }
    }

    /// <summary>最近一次呈现使用的窗口矩形，供截图与尺寸断言使用。</summary>
    public Rectangle LastBounds { get; private set; }

    /// <summary>最近一次实际画出来的条目数（12 条上限的验收凭据）。</summary>
    public int LastItemCount => _baseItems?.Count ?? 0;

    protected override void WndProc(ref Message m)
    {
        switch (m.Msg)
        {
            case NativeMethods.WM_NCHITTEST:
                m.Result = NativeMethods.HTTRANSPARENT;
                return;
            case NativeMethods.WM_MOUSEACTIVATE:
                m.Result = NativeMethods.MA_NOACTIVATE;
                return;
            case NativeMethods.WM_ERASEBKGND:
                m.Result = (IntPtr)1;
                return;
            case WmPageRequest:
                PageRequested?.Invoke(m.WParam.ToInt32());
                return;
        }

        base.WndProc(ref m);
    }

    // 内容全部经 UpdateLayeredWindow 提交，WinForms 自绘必须停用，否则会覆盖分层表面
    protected override void OnPaint(PaintEventArgs e)
    {
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
    }

    /// <summary>
    /// 呈现面板。同一份模型的高亮切换只重画高亮层——整帧重绘要 12–15ms，
    /// 每次按键都付这个代价会挤爆 §3.2 的 16ms 预算。
    /// </summary>
    private void PresentModel(Rectangle bounds, HudModel model, Combo? highlight, float scale)
    {
        LastBounds = bounds;
        _lastScale = scale;
        EnsureDeviceResources(bounds.Width, bounds.Height);
        EnsureCanvas(bounds.Width, bounds.Height);

        var graphics = _canvasGraphics!;
        bool baseReusable = ReferenceEquals(_baseModel, model)
                            && _baseItems is not null
                            && _baseWidth == bounds.Width
                            && Math.Abs(_baseScale - scale) < 0.001f;

        if (baseReusable)
        {
            // SourceCopy：直接连同 alpha 一起拷，避免半透明像素二次混合
            graphics.CompositingMode = CompositingMode.SourceCopy;
            graphics.DrawImageUnscaled(_baseCanvas!, 0, 0);
            graphics.CompositingMode = CompositingMode.SourceOver;
        }
        else
        {
            _baseItems = HudPanel.DrawBase(graphics, model, scale);
            _baseModel = model;
            _baseWidth = bounds.Width;
            _baseScale = scale;

            EnsureBaseCanvas(bounds.Width, bounds.Height);
            using var baseGraphics = Graphics.FromImage(_baseCanvas!);
            baseGraphics.CompositingMode = CompositingMode.SourceCopy;
            baseGraphics.DrawImageUnscaled(_canvas!, 0, 0);
        }

        if (highlight is { } combo)
        {
            HudPanel.DrawHighlight(graphics, _baseItems!, combo, scale, bounds.Width);
        }

        _lastModel = model;
        _lastHighlight = highlight;

        CopyToDib(_canvas!, _dibBits, bounds.Width, bounds.Height);
        ShowWithFade(bounds);
    }

    private void Present(Rectangle bounds, Action<Graphics> draw)
    {
        LastBounds = bounds;
        EnsureDeviceResources(bounds.Width, bounds.Height);
        EnsureCanvas(bounds.Width, bounds.Height);

        var graphics = _canvasGraphics!;
        graphics.Clear(Color.Transparent);
        draw(graphics);

        _lastModel = null;
        _lastHighlight = null;

        CopyToDib(_canvas!, _dibBits, bounds.Width, bounds.Height);
        ShowWithFade(bounds);
    }

    /// <summary>
    /// 首次出现走淡入；已经显示时（高亮切换、重新贴边）直接满不透明推上去，不重复动画。
    /// </summary>
    private void ShowWithFade(Rectangle bounds)
    {
        bool alreadyVisible = _isVisible;
        _isVisible = true;

        if (!alreadyVisible && _animationsEnabled())
        {
            FadeFramesPushed = 0;
            _fadeFrame = 0;
            PushLayered(bounds, 0);
            ShowNoActivateTopmost();
            _fadeTimer.Start();
            return;
        }

        _fadeTimer.Stop();
        FadeFramesPushed++;
        PushLayered(bounds, 255);
        ShowNoActivateTopmost();
    }

    private void OnFadeTick(object? sender, EventArgs e)
    {
        _fadeFrame++;
        double t = Math.Min(1.0, _fadeFrame / (double)FadeFrames);
        // ease-out：起步快、收尾缓，比线性更像「浮现」
        double eased = 1 - (1 - t) * (1 - t);
        int alpha = (int)Math.Round(255 * eased);

        PushLayered(LastBounds, alpha);
        FadeFramesPushed++;

        if (t >= 1)
        {
            _fadeTimer.Stop();
        }
    }

    private void ShowNoActivateTopmost()
    {
        NativeMethods.SetWindowPos(
            Handle, NativeMethods.HWND_TOPMOST, 0, 0, 0, 0,
            NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE
            | NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW);
        NativeMethods.ShowWindow(Handle, NativeMethods.SW_SHOWNOACTIVATE);
    }

    private void PushLayered(Rectangle bounds, int alpha)
    {
        IntPtr screenDc = NativeMethods.GetDC(IntPtr.Zero);
        if (screenDc == IntPtr.Zero)
        {
            throw new InvalidOperationException("GetDC(IntPtr.Zero) 返回 0");
        }

        try
        {
            var ptDst = new NativeMethods.POINT(bounds.X, bounds.Y);
            var size = new NativeMethods.SIZE(bounds.Width, bounds.Height);
            var ptSrc = new NativeMethods.POINT(0, 0);
            var blend = new NativeMethods.BLENDFUNCTION
            {
                BlendOp = NativeMethods.AC_SRC_OVER,
                BlendFlags = 0,
                SourceConstantAlpha = (byte)Math.Clamp(alpha, 0, 255),
                AlphaFormat = NativeMethods.AC_SRC_ALPHA,
            };

            if (!NativeMethods.UpdateLayeredWindow(
                    Handle, screenDc, ref ptDst, ref size, _memDc, ref ptSrc, 0, ref blend, NativeMethods.ULW_ALPHA))
            {
                throw new InvalidOperationException(
                    $"UpdateLayeredWindow 失败，Win32 错误码 {Marshal.GetLastWin32Error()}");
            }
        }
        finally
        {
            NativeMethods.ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    private void EnsureBaseCanvas(int width, int height)
    {
        if (_baseCanvas is not null && _baseCanvas.Width == width && _baseCanvas.Height == height)
        {
            return;
        }

        _baseCanvas?.Dispose();
        _baseCanvas = new Bitmap(width, height, PixelFormat.Format32bppPArgb);
    }

    private void EnsureCanvas(int width, int height)
    {
        if (_canvas is not null && _canvas.Width == width && _canvas.Height == height)
        {
            return;
        }

        _canvasGraphics?.Dispose();
        _canvas?.Dispose();
        _canvas = new Bitmap(width, height, PixelFormat.Format32bppPArgb);
        _canvasGraphics = Graphics.FromImage(_canvas);
        _canvasGraphics.SmoothingMode = SmoothingMode.AntiAlias;
        _canvasGraphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
    }

    private static void DrawDebugLabel(Graphics graphics, int width, int height, Color background, string label)
    {
        using (var path = RoundedRect(new Rectangle(0, 0, width, height), 12))
        using (var fill = new SolidBrush(background))
        using (var pen = new Pen(Color.FromArgb(90, 255, 255, 255), 1f))
        {
            graphics.FillPath(fill, path);
            graphics.DrawPath(pen, path);
        }

        using (var font = new Font("Segoe UI", 13f, FontStyle.Regular, GraphicsUnit.Pixel))
        using (var brush = new SolidBrush(Color.White))
        using (var format = new StringFormat
               {
                   Alignment = StringAlignment.Center,
                   LineAlignment = StringAlignment.Center,
               })
        {
            graphics.DrawString(label, font, brush, new RectangleF(0, 0, width, height), format);
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

    /// <summary>
    /// unsafe 仅用于逐行像素拷贝：UpdateLayeredWindow 要求 DIB 缓冲区里是预乘 alpha 的
    /// 32bpp 数据，用托管数组无法避免一次拷贝，且这里需要指针跨 stride 逐行搬（坑 #1）。
    /// </summary>
    private static unsafe void CopyToDib(Bitmap source, IntPtr destination, int width, int height)
    {
        BitmapData data = source.LockBits(
            new Rectangle(0, 0, width, height), ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
        try
        {
            int rowBytes = width * 4;
            byte* src = (byte*)data.Scan0;
            byte* dst = (byte*)destination;
            long stride = data.Stride;

            // 快路径：自上而下且行宽连续时一次拷完，省掉逐行调用开销
            if (stride == rowBytes)
            {
                Buffer.MemoryCopy(src, dst, (long)rowBytes * height, (long)rowBytes * height);
                return;
            }

            bool bottomUp = stride < 0;
            long absStride = bottomUp ? -stride : stride;

            for (int y = 0; y < height; y++)
            {
                // GDI+ 可能返回自下而上的位图（stride 为负），此时需反向取行
                long srcRow = bottomUp ? (height - 1 - y) * absStride : y * absStride;
                Buffer.MemoryCopy(src + srcRow, dst + (long)y * rowBytes, rowBytes, rowBytes);
            }
        }
        finally
        {
            source.UnlockBits(data);
        }
    }

    private void EnsureDeviceResources(int width, int height)
    {
        if (_dibSection != IntPtr.Zero && _dibWidth == width && _dibHeight == height)
        {
            return;
        }

        DestroyDeviceResources();

        var info = new NativeMethods.BITMAPINFO
        {
            bmiHeader = new NativeMethods.BITMAPINFOHEADER
            {
                biSize = (uint)Marshal.SizeOf<NativeMethods.BITMAPINFOHEADER>(),
                biWidth = width,
                biHeight = -height, // 负值 = 自上而下，与 Bitmap 行序一致
                biPlanes = 1,
                biBitCount = 32,
                biCompression = NativeMethods.BI_RGB,
            },
        };

        _memDc = NativeMethods.CreateCompatibleDC(IntPtr.Zero);
        if (_memDc == IntPtr.Zero)
        {
            throw new InvalidOperationException(
                $"CreateCompatibleDC 失败，Win32 错误码 {Marshal.GetLastWin32Error()}");
        }

        _dibSection = NativeMethods.CreateDIBSection(
            _memDc, ref info, NativeMethods.DIB_RGB_COLORS, out _dibBits, IntPtr.Zero, 0);
        if (_dibSection == IntPtr.Zero)
        {
            // 失败路径也要回收已创建的 DC，否则每次尺寸变化都漏一个 GDI 对象
            int error = Marshal.GetLastWin32Error();
            NativeMethods.DeleteDC(_memDc);
            _memDc = IntPtr.Zero;
            throw new InvalidOperationException($"CreateDIBSection 失败，Win32 错误码 {error}");
        }

        _oldBitmap = NativeMethods.SelectObject(_memDc, _dibSection);
        _dibWidth = width;
        _dibHeight = height;
    }

    private void DestroyDeviceResources()
    {
        if (_memDc != IntPtr.Zero)
        {
            if (_oldBitmap != IntPtr.Zero)
            {
                NativeMethods.SelectObject(_memDc, _oldBitmap);
                _oldBitmap = IntPtr.Zero;
            }

            NativeMethods.DeleteDC(_memDc);
            _memDc = IntPtr.Zero;
        }

        if (_dibSection != IntPtr.Zero)
        {
            NativeMethods.DeleteObject(_dibSection);
            _dibSection = IntPtr.Zero;
            _dibBits = IntPtr.Zero;
        }

        _dibWidth = 0;
        _dibHeight = 0;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _fadeTimer.Stop();
            _fadeTimer.Tick -= OnFadeTick;
            _fadeTimer.Dispose();
            _canvasGraphics?.Dispose();
            _canvas?.Dispose();
            _baseCanvas?.Dispose();
        }

        DestroyDeviceResources();
        base.Dispose(disposing);
    }
}
