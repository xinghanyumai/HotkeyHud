using System.Drawing.Drawing2D;
using System.Drawing.Text;
using HotkeyHud.Core;
using KeyNames = HotkeyHud.Core.Keys;

namespace HotkeyHud.App.UI;

/// <summary>
/// 面板的布局与绘制，不碰窗口句柄——窗口与分层合成在 <see cref="OverlayForm"/> 里。
/// 所有尺寸按逻辑像素（96 DPI）定义，绘制前统一乘 scale，避免 100%/150% 下出现两套数字。
/// <para>
/// 字体/画刷/画笔全部静态缓存：它们每帧都会用到，逐帧 new 会把渲染耗时推到 10ms 以上。
/// 这些对象随进程存活，数量固定（4–6 个字号），不做释放。
/// </para>
/// </summary>
internal static class HudPanel
{
    public const int MaxVisibleItems = 12;

    private const float PanelRadius = 14f;
    private const float PadX = 16f;
    private const float PadY = 14f;
    private const float HeaderHeight = 26f;
    private const float HeaderGap = 10f;
    private const float GroupTitleHeight = 20f;
    private const float RowHeight = 26f;
    private const float KeycapHeight = 22f;
    private const float KeycapRadius = 5f;
    private const float KeycapPadX = 6f;
    private const float KeycapMinWidth = 26f;
    private const float KeycapMaxRatio = 0.62f;
    private const float ColumnGap = 10f;
    private const int Columns = 3;
    private const float FooterHeight = 28f;
    private const float BaseWidth = 540f;
    private const float WorkAreaRatio = 0.72f;
    private const float ScreenMargin = 40f;
    private const float EmptyBodyHeight = 44f;

    // 规格 §6 色值
    private static readonly Color PanelBackground = Color.FromArgb(235, 0x16, 0x18, 0x1D);
    private static readonly Color TextPrimary = Color.FromArgb(255, 255, 255, 255);
    private static readonly Color TextSecondary = Color.FromArgb(140, 255, 255, 255);

    private static readonly SolidBrush PanelBrush = new(PanelBackground);
    private static readonly Pen PanelBorderPen = new(Color.FromArgb(26, 255, 255, 255), 1f);
    private static readonly SolidBrush TextPrimaryBrush = new(TextPrimary);
    private static readonly SolidBrush TextSecondaryBrush = new(TextSecondary);
    private static readonly SolidBrush KeycapBrush = new(Color.FromArgb(20, 255, 255, 255));
    private static readonly Pen KeycapPen = new(Color.FromArgb(41, 255, 255, 255), 1f);
    private static readonly Pen DividerPen = new(Color.FromArgb(26, 255, 255, 255), 1f);
    private static readonly SolidBrush RowHighlightBrush = new(Color.FromArgb(20, 255, 255, 255));
    private static readonly SolidBrush CtrlAccent = new(Color.FromArgb(0x4C, 0x8D, 0xFF));
    private static readonly SolidBrush AltAccent = new(Color.FromArgb(0xFF, 0x9F, 0x45));
    private static readonly SolidBrush ShiftAccent = new(Color.FromArgb(0x3D, 0xDC, 0x97));
    private static readonly SolidBrush WinAccent = new(Color.FromArgb(0x4C, 0xC9, 0xF0));

    private static readonly Dictionary<int, Font> FontCache = new();

    private static readonly StringFormat Centered = new()
    {
        Alignment = StringAlignment.Center,
        LineAlignment = StringAlignment.Center,
        FormatFlags = StringFormatFlags.NoWrap,
    };

    // 键帽被 KeycapMaxRatio 限宽时必须省略而不是硬裁，否则窄工作区下会看到半截文字
    private static readonly StringFormat CenteredEllipsis = new()
    {
        Alignment = StringAlignment.Center,
        LineAlignment = StringAlignment.Center,
        Trimming = StringTrimming.EllipsisCharacter,
        FormatFlags = StringFormatFlags.NoWrap,
    };

    private static readonly StringFormat LeftMiddle = new()
    {
        Trimming = StringTrimming.EllipsisCharacter,
        FormatFlags = StringFormatFlags.NoWrap,
        LineAlignment = StringAlignment.Center,
    };

    private static readonly StringFormat RightMiddle = new()
    {
        Alignment = StringAlignment.Far,
        LineAlignment = StringAlignment.Center,
        FormatFlags = StringFormatFlags.NoWrap,
    };

    private static SolidBrush AccentBrushOf(Modifier modifier) => modifier switch
    {
        Modifier.Alt => AltAccent,
        Modifier.Shift => ShiftAccent,
        Modifier.Win => WinAccent,
        _ => CtrlAccent,
    };

    /// <summary>面板尺寸由内容决定；宽度上限取「基准宽度」与「工作区 72%」的较小值。</summary>
    public static Size Measure(HudModel model, float scale, int workAreaWidth)
    {
        int width = (int)Math.Min(BaseWidth * scale, workAreaWidth * WorkAreaRatio);
        float height = PadY * 2 + HeaderHeight + HeaderGap;

        if (VisibleCount(model) == 0)
        {
            height += EmptyBodyHeight;
        }
        else
        {
            foreach ((bool showTitle, int itemCount) in GroupPlan(model))
            {
                if (showTitle)
                {
                    height += GroupTitleHeight;
                }

                height += RowsOf(itemCount) * RowHeight;
            }

            height += FooterHeight;
        }

        return new Size(width, (int)Math.Ceiling(height * scale));
    }

    public static Rectangle ComputeBounds(HudModel model, float scale, Rectangle workArea)
    {
        Size size = Measure(model, scale, workArea.Width);

        int x = workArea.X + (workArea.Width - size.Width) / 2;
        int y = workArea.Bottom - size.Height - (int)(ScreenMargin * scale);
        if (y < workArea.Top)
        {
            y = workArea.Top;
        }

        return new Rectangle(x, y, size.Width, size.Height);
    }

    /// <summary>
    /// 画完整面板（不含高亮），返回每个条目的行位置供高亮复用。
    /// 高亮是叠加上去的：同一份模型下只需重画高亮，不必整帧重绘（§3.2 的 16ms 预算靠这个守住）。
    /// </summary>
    public static IReadOnlyList<PlacedItem> DrawBase(Graphics graphics, HudModel model, float scale)
    {
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        graphics.Clear(Color.Transparent);

        int width = (int)Math.Round(graphics.VisibleClipBounds.Width);
        int height = (int)Math.Round(graphics.VisibleClipBounds.Height);

        using (var panelPath = RoundedRect(new RectangleF(0, 0, width, height), PanelRadius * scale))
        {
            graphics.FillPath(PanelBrush, panelPath);
            graphics.DrawPath(PanelBorderPen, panelPath);
        }

        float padX = PadX * scale;
        float padY = PadY * scale;
        float contentWidth = width - padX * 2;

        DrawHeader(graphics, model, scale, padX, padY, contentWidth);

        float y = padY + HeaderHeight * scale + HeaderGap * scale;

        if (VisibleCount(model) == 0)
        {
            DrawEmptyBody(graphics, model, scale, padX, y, contentWidth);
            return Array.Empty<PlacedItem>();
        }

        var placed = new List<PlacedItem>(MaxVisibleItems);
        y = DrawGroups(graphics, model, scale, padX, y, contentWidth, placed);

        DrawFooter(graphics, model, scale, padX, y, contentWidth);
        return placed;
    }

    /// <summary>只画高亮：行背景提亮 + 2px 竖条。不改变任何尺寸。</summary>
    public static void DrawHighlight(
        Graphics graphics, IReadOnlyList<PlacedItem> placed, Combo combo, float scale, int panelWidth)
    {
        string keys = KeyNames.Format(combo);
        PlacedItem? match = placed.FirstOrDefault(
            p => string.Equals(p.Keys, keys, StringComparison.Ordinal));
        if (match is null)
        {
            return;
        }

        graphics.FillRectangle(RowHighlightBrush, 0f, match.RowY, panelWidth, match.RowHeight);
        // 竖条放在命中格子的左缘而不是面板左缘：3 列同一行有 3 条时，放面板边缘会分不清命中的是哪个
        graphics.FillRectangle(AccentBrushOf(combo.Modifiers), match.CellX, match.RowY, 2f * scale, match.RowHeight);
    }

    // ---------------- 头部 ----------------

    private static void DrawHeader(
        Graphics graphics, HudModel model, float scale, float x, float y, float contentWidth)
    {
        string modifierName = model.Modifier == Modifier.None ? "—" : model.Modifier.ToString();
        Font capsFont = FontOf(11f * scale);
        Font headerFont = FontOf(12f * scale);

        SizeF capsText = graphics.MeasureString(modifierName, capsFont, PointF.Empty, StringFormat.GenericTypographic);
        float capsHeight = 20f * scale;
        float capsWidth = capsText.Width + 16f * scale;
        float capsTop = y + (HeaderHeight * scale - capsHeight) / 2;

        using (var capsPath = RoundedRect(new RectangleF(x, capsTop, capsWidth, capsHeight), capsHeight / 2))
        {
            graphics.FillPath(AccentBrushOf(model.Modifier), capsPath);
        }

        graphics.DrawString(
            modifierName, capsFont, TextPrimaryBrush,
            new RectangleF(x, capsTop, capsWidth, capsHeight), Centered);

        float titleX = x + capsWidth + 10f * scale;
        graphics.DrawString(
            model.AppName, headerFont, TextPrimaryBrush,
            new RectangleF(titleX, y, Math.Max(10f, x + contentWidth - titleX - 90f * scale), HeaderHeight * scale),
            LeftMiddle);

        graphics.DrawString(
            "松开隐藏", FontOf(11f * scale), TextSecondaryBrush,
            new RectangleF(x, y, contentWidth, HeaderHeight * scale), RightMiddle);
    }

    // ---------------- 分组与条目 ----------------

    private static float DrawGroups(
        Graphics graphics, HudModel model, float scale,
        float x, float y, float contentWidth, List<PlacedItem> placed)
    {
        List<(HudGroup Group, List<HudItem> Items, bool ShowTitle)> plan = BuildPlan(model);

        Font groupFont = FontOf(11f * scale);
        Font itemFont = FontOf(13f * scale);
        Font keyFont = FontOf(11f * scale);

        float columnWidth = (contentWidth - ColumnGap * scale * (Columns - 1)) / Columns;
        float keycapMaxWidth = columnWidth * KeycapMaxRatio;

        foreach ((HudGroup group, List<HudItem> items, bool showTitle) in plan)
        {
            if (showTitle)
            {
                graphics.DrawString(group.Title, groupFont, TextSecondaryBrush, new PointF(x, y + 2f * scale));
                y += GroupTitleHeight * scale;
            }

            for (int i = 0; i < items.Count; i++)
            {
                int column = i % Columns;
                int row = i / Columns;

                float cellX = x + column * (columnWidth + ColumnGap * scale);
                float cellY = y + row * RowHeight * scale;
                float keycapY = cellY + (RowHeight * scale - KeycapHeight * scale) / 2;

                // 每个键帽按自己的文字宽度算，不取全局最大——否则一个 Ctrl+Backspace
                // 会把整列键帽撑宽，所有动作文案都被省略号截断
                float measured = graphics.MeasureString(
                    items[i].Keys, keyFont, PointF.Empty, StringFormat.GenericTypographic).Width;
                float keycapWidth = Math.Min(
                    Math.Max(measured + KeycapPadX * 2 * scale, KeycapMinWidth * scale),
                    keycapMaxWidth);

                var keycap = new RectangleF(cellX, keycapY, keycapWidth, KeycapHeight * scale);
                using (var keycapPath = RoundedRect(keycap, KeycapRadius * scale))
                {
                    graphics.FillPath(KeycapBrush, keycapPath);
                    graphics.DrawPath(KeycapPen, keycapPath);
                }

                graphics.DrawString(items[i].Keys, keyFont, TextPrimaryBrush, keycap, CenteredEllipsis);

                float textX = keycap.Right + 6f * scale;
                graphics.DrawString(
                    items[i].Action, itemFont, TextPrimaryBrush,
                    new RectangleF(textX, cellY, Math.Max(10f, cellX + columnWidth - textX), RowHeight * scale),
                    LeftMiddle);

                placed.Add(new PlacedItem(items[i].Keys, cellY, RowHeight * scale, cellX, columnWidth));
            }

            y += RowsOf(items.Count) * RowHeight * scale;
        }

        return y;
    }

    // ---------------- 底部与空态 ----------------

    /// <summary>底部提示文案。抽成公开方法是为了让验收能断言文本本身，而不是只截图看。</summary>
    public static string FooterText(HudModel model)
    {
        int hidden = Math.Max(0, model.TotalCount - MaxVisibleItems);
        string modifierName = model.Modifier == Modifier.None ? "修饰键" : model.Modifier.ToString();
        string tail = $"松开 {modifierName} 隐藏";
        return hidden > 0 ? $"还有 {hidden} 个未显示 · {tail}" : tail;
    }

    private static void DrawFooter(
        Graphics graphics, HudModel model, float scale, float x, float y, float contentWidth)
    {
        graphics.DrawLine(DividerPen, x, y + 4f * scale, x + contentWidth, y + 4f * scale);

        graphics.DrawString(
            FooterText(model), FontOf(11f * scale), TextSecondaryBrush,
            new RectangleF(x, y + 8f * scale, contentWidth, 16f * scale), LeftMiddle);
    }

    private static void DrawEmptyBody(
        Graphics graphics, HudModel model, float scale, float x, float y, float contentWidth)
    {
        string modifierName = model.Modifier == Modifier.None ? "修饰键" : model.Modifier.ToString();
        string text = model.HasProfile
            ? $"该应用没有 {modifierName} 组合的快捷键数据"
            : $"未收录 {model.AppName}";

        graphics.DrawString(
            text, FontOf(13f * scale), TextSecondaryBrush,
            new RectangleF(x, y, contentWidth, EmptyBodyHeight * scale), LeftMiddle);
    }

    // ---------------- 规划与度量 ----------------

    private static int VisibleCount(HudModel model)
        => Math.Min(model.TotalCount, MaxVisibleItems);

    private static int RowsOf(int itemCount)
        => itemCount == 0 ? 0 : (itemCount + Columns - 1) / Columns;

    private static List<(HudGroup Group, List<HudItem> Items, bool ShowTitle)> BuildPlan(HudModel model)
    {
        bool showTitle = model.Groups.Count > 1;
        int remaining = MaxVisibleItems;
        var plan = new List<(HudGroup, List<HudItem>, bool)>();

        foreach (HudGroup group in model.Groups)
        {
            if (remaining <= 0)
            {
                break;
            }

            var items = new List<HudItem>();
            foreach (HudItem item in group.Items)
            {
                if (remaining <= 0)
                {
                    break;
                }

                items.Add(item);
                remaining--;
            }

            if (items.Count > 0)
            {
                plan.Add((group, items, showTitle));
            }
        }

        return plan;
    }

    private static IEnumerable<(bool ShowTitle, int ItemCount)> GroupPlan(HudModel model)
        => BuildPlan(model).Select(entry => (entry.ShowTitle, entry.Items.Count));

    private static Font FontOf(float pixelSize)
    {
        int key = (int)Math.Round(pixelSize);
        if (!FontCache.TryGetValue(key, out Font? font))
        {
            font = new Font("Segoe UI", key, FontStyle.Regular, GraphicsUnit.Pixel);
            FontCache[key] = font;
        }

        return font;
    }

    private static GraphicsPath RoundedRect(RectangleF bounds, float radius)
    {
        var path = new GraphicsPath();
        if (radius <= 0f)
        {
            path.AddRectangle(bounds);
            return path;
        }

        float d = radius * 2;
        path.AddArc(bounds.X, bounds.Y, d, d, 180, 90);
        path.AddArc(bounds.Right - d, bounds.Y, d, d, 270, 90);
        path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
        path.AddArc(bounds.X, bounds.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    /// <summary>一个已排版的条目：键文本 + 它所在行与格子的范围，供高亮定位。</summary>
    internal sealed class PlacedItem
    {
        public PlacedItem(string keys, float rowY, float rowHeight, float cellX, float cellWidth)
        {
            Keys = keys;
            RowY = rowY;
            RowHeight = rowHeight;
            CellX = cellX;
            CellWidth = cellWidth;
        }

        public string Keys { get; }

        public float RowY { get; }

        public float RowHeight { get; }

        public float CellX { get; }

        public float CellWidth { get; }
    }
}
