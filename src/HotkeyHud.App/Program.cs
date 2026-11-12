using System.Text;
using HotkeyHud.App.Shell;
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

            if (options.RenderGallery)
            {
                return RenderGallery.Run(
                    options.Out ?? Path.Combine(Path.GetTempPath(), "hotkeyhud-gallery"));
            }

            if (options.SuppressionTest)
            {
                return SuppressionTest.Run(
                    options.Out ?? Path.Combine(Path.GetTempPath(), "hotkeyhud-suppression"));
            }

            if (options.ShellTest)
            {
                return ShellTest.Run();
            }

            if (options.S6Test)
            {
                return S6Test.Run(
                    options.Out ?? Path.Combine(Path.GetTempPath(), "hotkeyhud-s6"));
            }

            if (options.ProbeSingleInstance)
            {
                // 供 ShellTest 跨进程验证：能拿到锁退出 0，拿不到退出 3
                using var probe = new SingleInstance();
                return probe.TryAcquire() ? 0 : 3;
            }

            if (options.Demo)
            {
                return RunDemo(options.Capture);
            }
#else
            _ = options; // Release 构建下诊断入口已编译剔除
#endif
            return RunTray();
        }
        catch (Exception ex)
        {
            // 坑 #11：WinExe 没有控制台，异常必须落盘，否则表现为「双击没反应」
            WriteStartupLog(ex);
            return 1;
        }
    }

    private static int RunTray()
    {
        ApplicationConfiguration.Initialize();

        using var instance = new SingleInstance();
        if (!instance.TryAcquire())
        {
            ShowAlreadyRunning();
            return 0;
        }

        using var shell = new ShellController();
        shell.ExitRequested += Application.ExitThread;
        Application.Run(new ApplicationContext());
        return 0;
    }

    /// <summary>第二个实例：弹一次气泡就退出，不产生常驻进程。</summary>
    private static void ShowAlreadyRunning()
    {
        using var icon = new NotifyIcon
        {
            Icon = SystemIcons.Information,
            Text = "HotkeyHud",
            Visible = true,
        };

        icon.BalloonTipTitle = "HotkeyHud 已在运行";
        icon.BalloonTipText = "见系统托盘，这里不再启动第二个实例。";
        icon.BalloonTipIcon = ToolTipIcon.Info;
        icon.ShowBalloonTip(3000);

        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (clock.ElapsedMilliseconds < 2500)
        {
            Application.DoEvents();
            Thread.Sleep(50);
        }
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
                Rectangle panel = HudApp.DebugPanelBounds();
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
        bool SelfTest, bool TypingTest, bool IntegrationTest, bool RenderGallery,
        bool SuppressionTest, bool ShellTest, bool S6Test, bool ProbeSingleInstance,
        bool Demo, int Seconds, string? Capture, string? Out);

    private static Options ParseOptions(string[] args)
    {
        bool selfTest = false;
        bool typingTest = false;
        bool integrationTest = false;
        bool renderGallery = false;
        bool suppressionTest = false;
        bool shellTest = false;
        bool s6Test = false;
        bool probeSingleInstance = false;
        bool demo = false;
        int seconds = 30;
        string? capture = null;
        string? output = null;

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

                case "--render-gallery":
                    RequireDiagnosticsBuild(arg);
                    renderGallery = true;
                    break;

                case "--suppression-test":
                    RequireDiagnosticsBuild(arg);
                    suppressionTest = true;
                    break;

                case "--shell-test":
                    RequireDiagnosticsBuild(arg);
                    shellTest = true;
                    break;

                case "--s6-test":
                    RequireDiagnosticsBuild(arg);
                    s6Test = true;
                    break;

                case "--probe-single-instance":
                    RequireDiagnosticsBuild(arg);
                    probeSingleInstance = true;
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
                    else if (TryReadValue(arg, "--out", out string? outputPath))
                    {
                        RequireDiagnosticsBuild(arg);
                        if (string.IsNullOrWhiteSpace(outputPath))
                        {
                            throw new ArgumentException("--out 需要非空目录");
                        }

                        output = outputPath;
                    }
                    else
                    {
                        throw new ArgumentException(
                            $"未知参数 \"{arg}\"。可用：--selftest --typing-test --integration-test "
                            + "--render-gallery --suppression-test --shell-test --s6-test --demo "
                            + "--seconds=N --capture=PATH --out=DIR"
                            + "（注意值必须用 = 连接）");
                    }

                    break;
            }
        }

        return new Options(selfTest, typingTest, integrationTest, renderGallery, suppressionTest,
            shellTest, s6Test, probeSingleInstance, demo, seconds, capture, output);
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
