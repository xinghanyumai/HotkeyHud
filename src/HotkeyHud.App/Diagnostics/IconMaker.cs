#if DEBUG
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Text;

namespace HotkeyHud.App.Diagnostics;

/// <summary>
/// 生成多尺寸 <c>app.ico</c>：圆角方底 <c>#4C8DFF</c> + 白色 <c>⌃</c>，线宽 2px@16。
/// 六档 16/20/24/32/48/256，同时供 exe 图标与托盘图标使用。
/// 用代码画而不是塞图片资源——§2.6 明确要求。
/// </summary>
internal static class IconMaker
{
    private static readonly int[] Sizes = { 16, 20, 24, 32, 48, 256 };

    public static int Run(string outputPath, string? screenshotDirectory)
    {
        ApplicationConfiguration.Initialize();
        try
        {
            Console.OutputEncoding = new UTF8Encoding(false);
        }
        catch
        {
            // 无控制台时忽略
        }

        var frames = new List<(int Size, byte[] Png)>(Sizes.Length);
        foreach (int size in Sizes)
        {
            frames.Add((size, RenderPng(size)));
        }

        WriteIco(outputPath, frames);
        Console.WriteLine($"[PASS] 生成 {outputPath}，{frames.Count} 档：{string.Join("/", Sizes)}");

        if (screenshotDirectory is not null)
        {
            Directory.CreateDirectory(screenshotDirectory);
            foreach (int size in new[] { 16, 32 })
            {
                // 放大 8 倍便于目视：像素块保持锐利，不加平滑
                using var icon = new Bitmap(size, size);
                using (var g = Graphics.FromImage(icon))
                using (var stream = new MemoryStream(RenderPng(size)))
                using (var source = Image.FromStream(stream))
                {
                    g.DrawImage(source, 0, 0, size, size);
                }

                using var scaled = new Bitmap(size * 8, size * 8);
                using (var g = Graphics.FromImage(scaled))
                {
                    g.InterpolationMode = InterpolationMode.NearestNeighbor;
                    g.PixelOffsetMode = PixelOffsetMode.Half;
                    g.DrawImage(icon, 0, 0, size * 8, size * 8);
                }

                string path = Path.Combine(screenshotDirectory, $"icon-{size}px.png");
                scaled.Save(path, ImageFormat.Png);
                Console.WriteLine($"[PASS] 截图 {path}（{size}px 放大 8 倍）");
            }
        }

        return 0;
    }

    private static byte[] RenderPng(int size)
    {
        using var bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.Clear(Color.Transparent);

            float radius = Math.Max(2f, size * 0.25f);
            using (var background = new SolidBrush(Color.FromArgb(0x4C, 0x8D, 0xFF)))
            using (var path = RoundedRect(new RectangleF(0, 0, size, size), radius))
            {
                graphics.FillPath(background, path);
            }

            // 白色 chevron ⌃：16px 下线宽 2，按比例放大
            float stroke = Math.Max(1.6f, size * 0.125f);
            float left = size * 0.26f;
            float right = size * 0.74f;
            float top = size * 0.38f;
            float bottom = size * 0.62f;
            float middle = size * 0.5f;

            using var pen = new Pen(Color.White, stroke)
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round,
                LineJoin = LineJoin.Round,
            };

            graphics.DrawLines(pen, new[]
            {
                new PointF(left, bottom),
                new PointF(middle, top),
                new PointF(right, bottom),
            });
        }

        using var stream = new MemoryStream();
        bitmap.Save(stream, ImageFormat.Png);
        return stream.ToArray();
    }

    private static GraphicsPath RoundedRect(RectangleF bounds, float radius)
    {
        float d = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(bounds.X, bounds.Y, d, d, 180, 90);
        path.AddArc(bounds.Right - d, bounds.Y, d, d, 270, 90);
        path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
        path.AddArc(bounds.X, bounds.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    /// <summary>ICO 容器：头部 + 每档一条目录项 + 各自的 PNG 数据（Vista 起支持 PNG 帧）。</summary>
    private static void WriteIco(string path, List<(int Size, byte[] Png)> frames)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream);

        writer.Write((ushort)0);              // reserved
        writer.Write((ushort)1);              // type = icon
        writer.Write((ushort)frames.Count);

        int offset = 6 + frames.Count * 16;
        foreach ((int size, byte[] png) in frames)
        {
            writer.Write((byte)(size >= 256 ? 0 : size));
            writer.Write((byte)(size >= 256 ? 0 : size));
            writer.Write((byte)0);            // 调色板数
            writer.Write((byte)0);            // reserved
            writer.Write((ushort)1);          // planes
            writer.Write((ushort)32);         // bpp
            writer.Write(png.Length);
            writer.Write(offset);
            offset += png.Length;
        }

        foreach ((_, byte[] png) in frames)
        {
            writer.Write(png);
        }
    }
}
#endif
