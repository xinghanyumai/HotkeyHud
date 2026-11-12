namespace HotkeyHud.App.Platform;

/// <summary>一块显示器的几何与缩放。抽成值类型是为了让定位逻辑能脱离真实硬件被断言。</summary>
public readonly record struct MonitorInfo(IntPtr Handle, Rectangle Bounds, Rectangle WorkArea, uint Dpi)
{
    public float Scale => Dpi <= 0 ? 1f : Dpi / 96f;
}

/// <summary>
/// 面板定位：先按光标挑屏，再按该屏的缩放算尺寸与位置，最后决定贴下缘还是贴上缘。
/// <para>
/// 纯函数——不碰 HWND、不查系统，验收可以直接喂一份「100% 主屏 + 150% 副屏」的合成布局。
/// </para>
/// </summary>
public static class ScreenAnchor
{
    public const float ScreenMargin = 40f;

    /// <summary>光标所在的屏；都不包含时取中心离光标最近的那块。</summary>
    public static MonitorInfo Pick(IReadOnlyList<MonitorInfo> monitors, Point cursor)
    {
        if (monitors.Count == 0)
        {
            return new MonitorInfo(IntPtr.Zero, new Rectangle(0, 0, 1920, 1080), new Rectangle(0, 0, 1920, 1080), 96);
        }

        foreach (MonitorInfo monitor in monitors)
        {
            if (monitor.Bounds.Contains(cursor))
            {
                return monitor;
            }
        }

        MonitorInfo best = monitors[0];
        long bestDistance = long.MaxValue;
        foreach (MonitorInfo monitor in monitors)
        {
            long dx = cursor.X - monitor.Bounds.X - monitor.Bounds.Width / 2;
            long dy = cursor.Y - monitor.Bounds.Y - monitor.Bounds.Height / 2;
            long distance = dx * dx + dy * dy;
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = monitor;
            }
        }

        return best;
    }

    /// <summary>
    /// 贴边：光标在工作区上半部 → 贴下缘；下半部 → 贴上缘。
    /// 视线跟着光标走，面板放远侧遮挡最少。
    /// </summary>
    public static Rectangle Anchor(MonitorInfo monitor, Size size, Point cursor)
    {
        Rectangle area = monitor.WorkArea;
        int margin = (int)Math.Round(ScreenMargin * monitor.Scale);

        int x = area.X + (area.Width - size.Width) / 2;
        bool cursorInLowerHalf = cursor.Y >= area.Y + area.Height / 2;
        int y = cursorInLowerHalf
            ? area.Y + margin
            : area.Bottom - size.Height - margin;

        // 夹回工作区：面板比工作区还高时至少保证顶边可见
        if (y + size.Height > area.Bottom)
        {
            y = area.Bottom - size.Height;
        }

        if (y < area.Y)
        {
            y = area.Y;
        }

        return new Rectangle(x, y, size.Width, size.Height);
    }
}
