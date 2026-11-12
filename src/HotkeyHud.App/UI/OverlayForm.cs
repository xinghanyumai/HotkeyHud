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

    public OverlayForm()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        SetStyle(ControlStyles.UserPaint | ControlStyles.Opaque, true);
    }

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

    /// <summary>按 §6 规格渲染快捷键面板。尺寸由内容决定，位置在光标所在屏的下缘居中。</summary>
    public void ShowModel(HudModel model, Combo? highlight)
    {
        Rectangle workArea = Screen.FromPoint(Cursor.Position).WorkingArea;
        float scale = DpiScale();
        Rectangle bounds = HudPanel.ComputeBounds(model, scale, workArea);
        PresentModel(bounds, model, highlight, scale);
    }

    /// <summary>S1 自检用的最简显示：一块纯色圆角 + 一行文字。不要删，--selftest 依赖它。</summary>
    public void ShowNoActivate(Rectangle bounds, Color background, string label)
    {
        Present(bounds, graphics => DrawDebugLabel(graphics, bounds.Width, bounds.Height, background, label));
    }

    public void HideOverlay()
    {
        if (IsHandleCreated)
        {
            NativeMethods.ShowWindow(Handle, NativeMethods.SW_HIDE);
        }
    }

    /// <summary>最近一次呈现使用的窗口矩形，供截图与尺寸断言使用。</summary>
    public Rectangle LastBounds { get; private set; }

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

        CopyToDib(_canvas!, _dibBits, bounds.Width, bounds.Height);
        PushLayered(bounds);
        ShowNoActivateTopmost();
    }

    private void Present(Rectangle bounds, Action<Graphics> draw)
    {
        LastBounds = bounds;
        EnsureDeviceResources(bounds.Width, bounds.Height);
        EnsureCanvas(bounds.Width, bounds.Height);

        var graphics = _canvasGraphics!;
        graphics.Clear(Color.Transparent);
        draw(graphics);

        CopyToDib(_canvas!, _dibBits, bounds.Width, bounds.Height);
        PushLayered(bounds);
        ShowNoActivateTopmost();
    }

    private void ShowNoActivateTopmost()
    {
        NativeMethods.SetWindowPos(
            Handle, NativeMethods.HWND_TOPMOST, 0, 0, 0, 0,
            NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE
            | NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW);
        NativeMethods.ShowWindow(Handle, NativeMethods.SW_SHOWNOACTIVATE);
    }

    private void PushLayered(Rectangle bounds)
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
                SourceConstantAlpha = 255,
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

    /// <summary>DPI 缩放系数。取窗口所在显示器的 DPI，拿不到时按 96 处理。</summary>
    private float DpiScale()
    {
        uint dpi = NativeMethods.GetDpiForWindow(Handle);
        if (dpi == 0)
        {
            dpi = (uint)DeviceDpi;
        }

        return dpi <= 0 ? 1f : dpi / 96f;
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
            _canvasGraphics?.Dispose();
            _canvas?.Dispose();
            _baseCanvas?.Dispose();
        }

        DestroyDeviceResources();
        base.Dispose(disposing);
    }
}
