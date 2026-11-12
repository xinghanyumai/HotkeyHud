#if DEBUG
using System.Drawing.Imaging;

namespace HotkeyHud.App.Diagnostics;

internal static class ScreenCapture
{
    public static void CaptureTo(Rectangle area, string path)
    {
        using var bitmap = new Bitmap(area.Width, area.Height, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.CopyFromScreen(area.Left, area.Top, 0, 0, area.Size, CopyPixelOperation.SourceCopy);
        }

        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        bitmap.Save(path, ImageFormat.Png);
    }
}
#endif
