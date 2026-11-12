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

    /// <summary>组块超过这个高度才允许拆分并加「（续）」。超过它面板会高得离谱。</summary>
    private const int MaxRowsBeforeSplit = 8;

    // 规格 §6.1 色值
    // 规格 §6.1 v1.2：面板完全不透明。无背景模糊时，半透明只产生穿透噪声、不产生美感收益
    private static readonly Color PanelBackground = Color.FromArgb(255, 0x16, 0x18, 0x1D);
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

        List<Block> blocks = BuildBlocks(model);
        if (blocks.Count == 0)
        {
            height += EmptyBodyHeight;
        }
        else
        {
            height += Pack(blocks).Rows * RowHeight + FooterHeight;
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

        List<Block> blocks = BuildBlocks(model);
        if (blocks.Count == 0)
        {
            DrawEmptyBody(graphics, model, scale, padX, y, contentWidth);
            return Array.Empty<PlacedItem>();
        }

        var placed = new List<PlacedItem>(MaxVisibleItems);
        (List<Cell>[] columns, int rows) = Pack(blocks);
        DrawCells(graphics, columns, scale, padX, y, contentWidth, placed);

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

    /// <summary>
    /// 每一列最顶上的那个元素是什么：组名，或 null（单组模型不显示组名）。
    /// §6.1 v1.2 要求「不存在无组名的续列」，验收直接断言这个返回值。
    /// </summary>
    public static IReadOnlyList<string?> ColumnLeaders(HudModel model)
    {
        List<Block> blocks = BuildBlocks(model);
        if (blocks.Count == 0)
        {
            return Array.Empty<string?>();
        }

        (List<Cell>[] columns, _) = Pack(blocks);
        var leaders = new List<string?>(Columns);
        foreach (List<Cell> column in columns)
        {
            if (column.Count > 0)
            {
                leaders.Add(column[0].GroupTitle);
            }
        }

        return leaders;
    }

    /// <summary>每一列实际占用的行数，用于验收「三列高度差 ≤ 2 行」。</summary>
    public static IReadOnlyList<int> ColumnHeights(HudModel model)
    {
        List<Block> blocks = BuildBlocks(model);
        if (blocks.Count == 0)
        {
            return Array.Empty<int>();
        }

        (List<Cell>[] columns, _) = Pack(blocks);
        var heights = new List<int>(Columns);
        foreach (List<Cell> column in columns)
        {
            heights.Add(column.Count);
        }

        return heights;
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
    /// 把模型切成「组块」：组名 + 该组条目是一个不可分的整体（§6.1 v1.2 组块不可跨列）。
    /// 条目总数受 <see cref="MaxVisibleItems"/> 限制，组名不计入上限。
    /// </summary>
    private static List<Block> BuildBlocks(HudModel model)
    {
        bool showTitle = model.Groups.Count > 1;
        int remaining = MaxVisibleItems;
        var blocks = new List<Block>(model.Groups.Count);

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
                blocks.Add(new Block(showTitle ? group.Title : null, items));
            }
        }

        return SplitOversized(blocks);
    }

    /// <summary>
    /// 组块高到会让面板异常高时才拆开，续块加「（续）」（§6.1 v1.2 的兜底条款）。
    /// 常规数据（每组 ≤ 8 格）走不到这里。
    /// </summary>
    private static List<Block> SplitOversized(List<Block> blocks)
    {
        var result = new List<Block>(blocks.Count);

        foreach (Block block in blocks)
        {
            if (block.Height <= MaxRowsBeforeSplit || block.Title is null)
            {
                result.Add(block);
                continue;
            }

            // 首块要留一格给组名，续块也要留一格给「（续）」
            int index = 0;
            string title = block.Title;
            while (index < block.Items.Count)
            {
                int capacity = MaxRowsBeforeSplit - 1;
                var chunk = block.Items.Skip(index).Take(capacity).ToArray();
                result.Add(new Block(title, chunk));
                index += chunk.Length;
                title = block.Title + "（续）";
            }
        }

        return result;
    }

    /// <summary>
    /// 组块按「当前最短列优先」装填（§6.1 v1.3）。装不下就拆，续块带「组名（续）」
    /// 且紧跟其后——这样不会出现无组名的续列，也不会出现两列半空。
    /// </summary>
    private static (List<Cell>[] Columns, int Rows) Pack(List<Block> blocks)
    {
        int total = 0;
        foreach (Block block in blocks)
        {
            total += block.Height;
        }

        int capacity = RowsOf(total);
        while (true)
        {
            (List<Cell>[] Columns, int Rows)? packed = TryPack(blocks, capacity);
            if (packed is not null)
            {
                return packed.Value;
            }

            capacity++;
        }
    }

    private static (List<Cell>[] Columns, int Rows)? TryPack(List<Block> blocks, int capacity)
    {
        var columns = new List<Cell>[Columns];
        var heights = new int[Columns];
        var full = new bool[Columns];
        for (int i = 0; i < Columns; i++)
        {
            columns[i] = new List<Cell>(capacity);
        }

        var pending = new List<Block>(blocks);
        while (pending.Count > 0)
        {
            int target = ShortestColumn(heights, full);
            if (target < 0)
            {
                return null; // 三列都满了还装不下，抬高 capacity 重试
            }

            Block block = pending[0];
            int remaining = capacity - heights[target];
            int titleCells = block.Title is null ? 0 : 1;

            if (block.Height <= remaining)
            {
                Append(columns[target], block);
                heights[target] += block.Height;
                pending.RemoveAt(0);
                continue;
            }

            // 剩余空间连「组名 + 1 条」都放不下：这一列就此封口
            if (remaining < titleCells + 1)
            {
                full[target] = true;
                continue;
            }

            int itemCount = remaining - titleCells;
            var chunk = new Block(block.Title, block.Items.Take(itemCount).ToArray());
            Append(columns[target], chunk);
            heights[target] += chunk.Height;
            pending.RemoveAt(0);

            HudItem[] rest = block.Items.Skip(itemCount).ToArray();
            if (rest.Length > 0)
            {
                pending.Insert(0, new Block(ContinueTitle(block.Title), rest));
            }

            full[target] = true;
        }

        int rows = 0;
        for (int i = 0; i < Columns; i++)
        {
            rows = Math.Max(rows, heights[i]);
        }

        return (columns, rows);
    }

    private static int ShortestColumn(int[] heights, bool[] full)
    {
        int best = -1;
        for (int i = 0; i < heights.Length; i++)
        {
            if (full[i])
            {
                continue;
            }

            if (best < 0 || heights[i] < heights[best])
            {
                best = i;
            }
        }

        return best;
    }

    private static void Append(List<Cell> column, Block block)
    {
        if (block.Title is not null)
        {
            column.Add(Cell.Title(block.Title));
        }

        foreach (HudItem item in block.Items)
        {
            column.Add(Cell.Entry(item));
        }
    }

    private static string? ContinueTitle(string? title)
        => title is null || title.EndsWith("（续）", StringComparison.Ordinal)
            ? title
            : title + "（续）";

    private static int RowsOf(int cellCount)
        => cellCount == 0 ? 0 : (cellCount + Columns - 1) / Columns;

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

    /// <summary>列内排布的基本单位：要么是组名，要么是一个条目。</summary>
    private readonly record struct Cell(string? GroupTitle, HudItem? Item)
    {
        public static Cell Title(string title) => new(title, null);

        public static Cell Entry(HudItem item) => new(null, item);
    }

    /// <summary>不可跨列的整体：组名 + 该组条目。<c>Title</c> 为 null 表示单组、不显示组名。</summary>
    private sealed record Block(string? Title, IReadOnlyList<HudItem> Items)
    {
        public int Height => (Title is null ? 0 : 1) + Items.Count;
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
