using HotkeyHud.App.Diagnostics;

namespace HotkeyHud.App;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            if (HasFlag(args, "--selftest"))
            {
                return SelfTest.Run();
            }

            if (HasFlag(args, "--typing-test"))
            {
                return TypingTest.Run(GetInt(args, "--seconds", 30), GetValue(args, "--capture"));
            }

            if (HasFlag(args, "--demo"))
            {
                return RunDemo(GetValue(args, "--capture"));
            }

            return RunInteractive();
        }
        catch (Exception ex)
        {
            // 坑 #11：WinExe 没有控制台，异常必须落盘，否则表现为「双击没反应」
            WriteStartupLog(ex);
            return 1;
        }
    }

    private static int RunInteractive()
    {
        ApplicationConfiguration.Initialize();
        using var hud = new HudApp();
        Application.Run(new ApplicationContext());
        return 0;
    }

    private static int RunDemo(string? capturePath)
    {
        ApplicationConfiguration.Initialize();
        using var hud = new HudApp();
        hud.ShowOverlay();

        var timer = new System.Windows.Forms.Timer { Interval = capturePath is null ? 3000 : 700 };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            timer.Dispose();

            if (capturePath is not null)
            {
                Rectangle area = Screen.PrimaryScreen?.Bounds ?? new Rectangle(0, 0, 1920, 1080);
                var panel = new Rectangle(
                    area.X + (area.Width - 420) / 2,
                    area.Y + (area.Height - 180) / 2,
                    420,
                    180);
                panel.Inflate(30, 30);
                ScreenCapture.CaptureTo(panel, capturePath);
            }

            hud.HideOverlay();
            Application.Exit();
        };
        timer.Start();

        Application.Run(new ApplicationContext());
        return 0;
    }

    private static bool HasFlag(string[] args, string flag)
        => args.Any(a => string.Equals(a, flag, StringComparison.OrdinalIgnoreCase));

    private static string? GetValue(string[] args, string name)
    {
        string? match = args.FirstOrDefault(
            a => a.StartsWith(name + "=", StringComparison.OrdinalIgnoreCase));
        return match is null ? null : match[(name.Length + 1)..];
    }

    private static int GetInt(string[] args, string name, int fallback)
        => int.TryParse(GetValue(args, name), out int value) ? value : fallback;

    private static void WriteStartupLog(Exception ex)
    {
        try
        {
            string path = Path.Combine(Path.GetTempPath(), "hotkeyhud-startup.log");
            File.AppendAllText(
                path,
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {ex}{Environment.NewLine}");
        }
        catch
        {
            // 日志失败不能再抛，否则连退出码都拿不到
        }
    }
}
