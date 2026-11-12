using System.Text;
#if DEBUG
using HotkeyHud.App.Diagnostics;
#endif

namespace HotkeyHud.App;

internal static class Program
{
    private const int ExitUsageError = 2;

    [STAThread]
    private static int Main(string[] args)
    {
        Options options;
        try
        {
            options = ParseOptions(args);
        }
        catch (ArgumentException ex)
        {
            // 未知/畸形参数必须显式失败，不能静默忽略（S1 整改第 4 项）
            WriteError(ex.Message);
            return ExitUsageError;
        }

        try
        {
#if DEBUG
            if (options.SelfTest)
            {
                return SelfTest.Run();
            }

            if (options.TypingTest)
            {
                return TypingTest.Run(options.Seconds, options.Capture);
            }

            if (options.IntegrationTest)
            {
                return IntegrationTest.Run();
            }

            if (options.Demo)
            {
                return RunDemo(options.Capture);
            }
#else
            _ = options; // Release 构建下诊断入口已编译剔除
#endif
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

#if DEBUG
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
                Rectangle panel = HudApp.PanelBounds();
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
#endif

    private sealed record Options(
        bool SelfTest, bool TypingTest, bool IntegrationTest, bool Demo, int Seconds, string? Capture);

    private static Options ParseOptions(string[] args)
    {
        bool selfTest = false;
        bool typingTest = false;
        bool integrationTest = false;
        bool demo = false;
        int seconds = 30;
        string? capture = null;

        foreach (string arg in args)
        {
            switch (arg)
            {
                case "--selftest":
                    RequireDiagnosticsBuild(arg);
                    selfTest = true;
                    break;

                case "--typing-test":
                    RequireDiagnosticsBuild(arg);
                    typingTest = true;
                    break;

                case "--integration-test":
                    RequireDiagnosticsBuild(arg);
                    integrationTest = true;
                    break;

                case "--demo":
                    RequireDiagnosticsBuild(arg);
                    demo = true;
                    break;

                default:
                    if (TryReadValue(arg, "--seconds", out string? rawSeconds))
                    {
                        RequireDiagnosticsBuild(arg);
                        if (!int.TryParse(rawSeconds, out seconds) || seconds <= 0)
                        {
                            throw new ArgumentException(
                                $"--seconds 需要正整数，收到 \"{rawSeconds}\"");
                        }
                    }
                    else if (TryReadValue(arg, "--capture", out string? capturePath))
                    {
                        RequireDiagnosticsBuild(arg);
                        if (string.IsNullOrWhiteSpace(capturePath))
                        {
                            throw new ArgumentException("--capture 需要非空路径");
                        }

                        capture = capturePath;
                    }
                    else
                    {
                        throw new ArgumentException(
                            $"未知参数 \"{arg}\"。可用：--selftest --typing-test --integration-test "
                            + "--demo --seconds=N --capture=PATH（注意值必须用 = 连接）");
                    }

                    break;
            }
        }

        return new Options(selfTest, typingTest, integrationTest, demo, seconds, capture);
    }

    /// <summary>只接受 <c>--name=value</c>；空格分隔形式一律落到「未知参数」分支报错。</summary>
    private static bool TryReadValue(string arg, string name, out string? value)
    {
        string prefix = name + "=";
        if (arg.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            value = arg[prefix.Length..];
            return true;
        }

        value = null;
        return false;
    }

    private static void RequireDiagnosticsBuild(string arg)
    {
#if !DEBUG
        throw new ArgumentException($"{arg} 只在 Debug 构建可用");
#endif
    }

    private static void WriteError(string message)
    {
        try
        {
            using var stderr = Console.OpenStandardError();
            // 不带 BOM：UTF8Encoding(true) 会在管道开头插入 EF BB BF，污染下游解析
            using var writer = new StreamWriter(stderr, new UTF8Encoding(false)) { AutoFlush = true };
            writer.WriteLine(message);
        }
        catch
        {
            // 无标准错误流时只能靠退出码传达
        }
    }

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
