using System.Drawing.Drawing2D;
using System.Drawing.Text;
using HotkeyHud.Core;
using KeyNames = HotkeyHud.Core.Keys;

namespace HotkeyHud.App.UI;

/// <summary>
/// 面板的布局与绘制，不碰窗口句柄——窗口与分层合成在 <see cref="OverlayForm"/> 里。
/// 所有尺寸按逻辑像素（96 DPI）定义，绘制前统一乘 scale，避免 100%/150% 下出现两套数字。
/// <para>
/// 视觉规格唯一来源：规格 §6.1。键帽只显示非修饰键（修饰键已由头部胶囊表达），
/// 分组按列内流式排布（组名占一格，不强制换行）。
/// </para>
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
    private const float RowHeight = 26f;
    private const float KeycapHeight = 22f;
    private const float KeycapRadius = 5f;
    private const float KeycapPadX = 6f;
    private const float KeycapMinWidth = 26f;
    private const float KeycapMaxRatio = 0.62f;
    private const float ColumnGap = 12f;
    private const int Columns = 3;
    private const float FooterHeight = 28f;
    private const float CapsuleHeight = 22f;
    private const float CapsuleRadius = 5f;
    private const float BaseWidth = 540f;
    private const float WorkAreaRatio = 0.72f;
    private const float ScreenMargin = 40f;
    private const float EmptyBodyHeight = 44f;

    // 规格 §6.1 色值
    private static readonly Color PanelBackground = Color.FromArgb(245, 0x16, 0x18, 0x1D); // 96%
    private static readonly Color TextPrimary = Color.FromArgb(255, 255, 255, 255);
    private static readonly Color TextSecondary = Color.FromArgb(140, 255, 255, 255); // 55%

    private static readonly SolidBrush PanelBrush = new(PanelBackground);
    private static readonly Pen PanelBorderPen = new(Color.FromArgb(26, 255, 255, 255), 1f);
    private static readonly SolidBrush TextPrimaryBrush = new(TextPrimary);
    private static readonly SolidBrush TextSecondaryBrush = new(TextSecondary);
    private static readonly SolidBrush KeycapBrush = new(Color.FromArgb(23, 255, 255, 255)); // 9%
    private static readonly Pen KeycapPen = new(Color.FromArgb(41, 255, 255, 255), 1f); // 16%
    private static readonly Pen DividerPen = new(Color.FromArgb(26, 255, 255, 255), 1f);
    private static readonly SolidBrush RowHighlightBrush = new(Color.FromArgb(20, 255, 255, 255)); // 8%
    private static readonly SolidBrush CtrlAccent = new(Color.FromArgb(0x4C, 0x8D, 0xFF));
    private static readonly SolidBrush AltAccent = new(Color.FromArgb(0xFF, 0x9F, 0x45));
    private static readonly SolidBrush ShiftAccent = new(Color.FromArgb(0x3D, 0xDC, 0x97));
    private static readonly SolidBrush WinAccent = new(Color.FromArgb(0x4C, 0xC9, 0xF0));

    private static readonly Dictionary<int, Font> FontCache = new();

    /// <summary>§6.1 缩写表。长键名会挤掉动作文案，缩写后键帽宽度从约 100px 降到约 50px。</summary>
    private static readonly Dictionary<string, string> KeyAbbreviations = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Backspace"] = "Bksp",
        ["PageUp"] = "PgUp",
        ["PageDown"] = "PgDn",
        ["Delete"] = "Del",
        ["Insert"] = "Ins",
        ["PrintScreen"] = "PrtSc",
    };

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

    /// <summary>
    /// 面板尺寸由内容决定：格数 = 条目数 + 组名数，按 3 列分摊后取行数。
    /// 宽度上限取「基准宽度」与「工作区 72%」的较小值。
    /// </summary>
    public static Size Measure(HudModel model, float scale, int workAreaWidth)
    {
        int width = (int)Math.Min(BaseWidth * scale, workAreaWidth * WorkAreaRatio);
        float height = PadY * 2 + HeaderHeight + HeaderGap;

        List<Cell> cells = BuildCells(model);
        if (cells.Count == 0)
        {
            height += EmptyBodyHeight;
        }
        else
        {
            // 打包后实际行数可能比 ceil(格数/3) 多一行（组名让位），高度必须按实际行数算
            height += PackedRows(cells) * RowHeight + FooterHeight;
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
    /// 高亮是叠加上去的：同一份模型下只需重画高亮，不必整帧重绘（§6.1 的性能门槛靠这个守住）。
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

        List<Cell> cells = BuildCells(model);
        if (cells.Count == 0)
        {
            DrawEmptyBody(graphics, model, scale, padX, y, contentWidth);
            return Array.Empty<PlacedItem>();
        }

        var placed = new List<PlacedItem>(MaxVisibleItems);
        int rows = PackedRows(cells);
        DrawCells(graphics, PackColumns(cells, RowsOf(cells.Count)), scale, padX, y, contentWidth, placed);

        DrawFooter(graphics, model, scale, padX, y + rows * RowHeight * scale, contentWidth);
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
        // 竖条放在命中格子的左缘而不是面板左缘：同一行有 3 格时，放面板边缘会分不清命中的是哪个
        graphics.FillRectangle(AccentBrushOf(combo.Modifiers), match.CellX, match.RowY, 2f * scale, match.RowHeight);
    }

    /// <summary>底部提示文案。抽成公开方法是为了让验收能断言文本本身，而不是只截图看。</summary>
    public static string FooterText(HudModel model)
    {
        int hidden = Math.Max(0, model.TotalCount - MaxVisibleItems);
        string modifierName = model.Modifier == Modifier.None ? "修饰键" : model.Modifier.ToString();
        string tail = $"松开 {modifierName} 隐藏";
        return hidden > 0 ? $"还有 {hidden} 个未显示 · {tail}" : tail;
    }

    /// <summary>键帽只显示非修饰键，并套用 §6.1 缩写表。</summary>
    public static string KeycapText(string keys)
    {
        int index = keys.LastIndexOf('+');
        string key = index >= 0 ? keys[(index + 1)..] : keys;
        return KeyAbbreviations.TryGetValue(key, out string? shortName) ? shortName : key;
    }

    // ---------------- 头部 ----------------

    private static void DrawHeader(
        Graphics graphics, HudModel model, float scale, float x, float y, float contentWidth)
    {
        string modifierName = model.Modifier == Modifier.None ? "—" : model.Modifier.ToString();
        Font capsFont = FontOf(11f * scale);
        Font headerFont = FontOf(12f * scale);

        SizeF capsText = graphics.MeasureString(modifierName, capsFont, PointF.Empty, StringFormat.GenericTypographic);
        float capsHeight = CapsuleHeight * scale;
        float capsWidth = capsText.Width + 16f * scale;
        float capsTop = y + (HeaderHeight * scale - capsHeight) / 2;

        using (var capsPath = RoundedRect(
                   new RectangleF(x, capsTop, capsWidth, capsHeight), CapsuleRadius * scale))
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

    // ---------------- 单元格（组名 / 条目）----------------

    private static void DrawCells(
        Graphics graphics, List<Cell>[] columns, float scale,
        float x, float y, float contentWidth, List<PlacedItem> placed)
    {
        Font groupFont = FontOf(11f * scale);
        Font itemFont = FontOf(13f * scale);
        Font keyFont = FontOf(11f * scale);

        float columnWidth = (contentWidth - ColumnGap * scale * (Columns - 1)) / Columns;
        float keycapMaxWidth = columnWidth * KeycapMaxRatio;

        for (int column = 0; column < columns.Length; column++)
        {
            for (int row = 0; row < columns[column].Count; row++)
            {
                Cell cell = columns[column][row];
                float cellX = x + column * (columnWidth + ColumnGap * scale);
                float cellY = y + row * RowHeight * scale;

                if (cell.GroupTitle is { } title)
                {
                    graphics.DrawString(
                        title, groupFont, TextSecondaryBrush,
                        new RectangleF(cellX, cellY, columnWidth, RowHeight * scale), LeftMiddle);
                    continue;
                }

                HudItem item = cell.Item!;
                string keyText = KeycapText(item.Keys);
                float keycapY = cellY + (RowHeight * scale - KeycapHeight * scale) / 2;

                // 每个键帽按自己的文字宽度算，不取全局最大——否则一个长键名会把整列键帽撑宽
                float measured = graphics.MeasureString(
                    keyText, keyFont, PointF.Empty, StringFormat.GenericTypographic).Width;
                float keycapWidth = Math.Min(
                    Math.Max(measured + KeycapPadX * 2 * scale, KeycapMinWidth * scale),
                    keycapMaxWidth);

                var keycap = new RectangleF(cellX, keycapY, keycapWidth, KeycapHeight * scale);
                using (var keycapPath = RoundedRect(keycap, KeycapRadius * scale))
                {
                    graphics.FillPath(KeycapBrush, keycapPath);
                    graphics.DrawPath(KeycapPen, keycapPath);
                }

                graphics.DrawString(keyText, keyFont, TextPrimaryBrush, keycap, CenteredEllipsis);

                float textX = keycap.Right + 6f * scale;
                graphics.DrawString(
                    item.Action, itemFont, TextPrimaryBrush,
                    new RectangleF(textX, cellY, Math.Max(10f, cellX + columnWidth - textX), RowHeight * scale),
                    LeftMiddle);

                placed.Add(new PlacedItem(item.Keys, cellY, RowHeight * scale, cellX));
            }
        }
    }

    // ---------------- 底部与空态 ----------------

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

    /// <summary>
    /// 把模型摊平成一串单元格：组名一格 + 该组条目各一格。
    /// 条目总数受 <see cref="MaxVisibleItems"/> 限制，组名不计入上限。
    /// </summary>
    private static List<Cell> BuildCells(HudModel model)
    {
        bool showTitle = model.Groups.Count > 1;
        int remaining = MaxVisibleItems;
        var cells = new List<Cell>(MaxVisibleItems + model.Groups.Count);

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

            if (items.Count == 0)
            {
                continue;
            }

            if (showTitle)
            {
                cells.Add(Cell.Title(group.Title));
            }

            foreach (HudItem item in items)
            {
                cells.Add(Cell.Entry(item));
            }
        }

        return cells;
    }

    /// <summary>
    /// 列内流式打包：先填满第 1 列再换列。
    /// <para>
    /// 比 §6.1 的原始算法多一条约束：**组名不得落在列底**。否则组名留在上一列末尾、
    /// 它的条目全在下一列顶部，会被读成上一组的条目（实测 Chrome 面板的「网页」组就是这样）。
    /// 遇到这种情况让该组名从下一列开头起，列内格数允许不齐。
    /// </para>
    /// </summary>
    private static List<Cell>[] PackColumns(List<Cell> cells, int rows)
    {
        var columns = new List<Cell>[Columns];
        int index = 0;

        for (int column = 0; column < Columns; column++)
        {
            var bucket = new List<Cell>(rows);
            while (index < cells.Count && bucket.Count < rows)
            {
                // 组名若将落在本列最后一行，且后面还有条目，就把它挪到下一列开头
                bool orphanTitle = cells[index].GroupTitle is not null
                                   && bucket.Count == rows - 1
                                   && index + 1 < cells.Count;
                if (orphanTitle)
                {
                    break;
                }

                bucket.Add(cells[index]);
                index++;
            }

            columns[column] = bucket;
        }

        // 极端情况下（组特别多）3 列装不下，把剩下的都塞进最后一列，宁可溢出也不丢内容
        while (index < cells.Count)
        {
            columns[^1].Add(cells[index]);
            index++;
        }

        return columns;
    }

    private static int RowsOf(int cellCount)
        => cellCount == 0 ? 0 : (cellCount + Columns - 1) / Columns;

    /// <summary>打包后真正占用的行数（= 最长一列的格数）。</summary>
    private static int PackedRows(List<Cell> cells)
    {
        if (cells.Count == 0)
        {
            return 0;
        }

        List<Cell>[] columns = PackColumns(cells, RowsOf(cells.Count));
        int rows = 0;
        foreach (List<Cell> column in columns)
        {
            rows = Math.Max(rows, column.Count);
        }

        return rows;
    }

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

    /// <summary>列内流式排布的基本单位：要么是组名，要么是一个条目。</summary>
    private readonly record struct Cell(string? GroupTitle, HudItem? Item)
    {
        public static Cell Title(string title) => new(title, null);

        public static Cell Entry(HudItem item) => new(null, item);
    }

    /// <summary>一个已排版的条目：键文本 + 它所在行与格子的位置，供高亮定位。</summary>
    internal sealed class PlacedItem
    {
        public PlacedItem(string keys, float rowY, float rowHeight, float cellX)
        {
            Keys = keys;
            RowY = rowY;
            RowHeight = rowHeight;
            CellX = cellX;
        }

        public string Keys { get; }

        public float RowY { get; }

        public float RowHeight { get; }

        public float CellX { get; }
    }
}
