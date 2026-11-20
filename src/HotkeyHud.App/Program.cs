using System.Text;
using System.Diagnostics;
using HotkeyHud.App.Shell;
using HotkeyHud.Core;
using HotkeyHud.App.Shell.Adapters;
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

            if (options.PagingTest)
            {
                return PagingTest.Run(
                    options.Out ?? Path.Combine(Path.GetTempPath(), "hotkeyhud-paging"));
            }

            if (options.MakeIcon)
            {
                return IconMaker.Run(
                    Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "app.ico"),
                    options.Out);
            }

            if (options.HookProbe)
            {
                return HookProbe.Run();
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

            // 整屏截图：供人工/自动化拍「面板叠在真实应用上」的证据。
            // 本进程带 PerMonitorV2 清单，抓到的才是物理像素，不会像 DPI-unaware 进程那样只拿到左上角。
            if (options.AdapterCheck)
            {
                return RunAdapterCheck();
            }

            if (options.SettingsDemo)
            {
                return RunSettingsDemo();
            }

            if (options.TemplatePreview is { } previewExe)
            {
                Console.OutputEncoding = new UTF8Encoding(false);
                Console.WriteLine(UserApps.Template(previewExe));
                return 0;
            }

            if (options.ScreenShot is { } shotPath)
            {
                ApplicationConfiguration.Initialize();
                ScreenCapture.CaptureTo(SystemInformation.VirtualScreen, shotPath);
                return 0;
            }

            // --diag 只是打开诊断日志，然后照常进托盘模式
            if (options.Diag)
            {
                ShowDiagnostics.Enable();
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

    /// <summary>用真实 %APPDATA% 跑一遍适配器，验收「读得出用户改过的键位」用。</summary>
    private static int RunAdapterCheck()
    {
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

        Check("Code.exe", @"C://Program Files//Microsoft VS Code//Code.exe", appData);
        Check("idea64.exe", @"C://Program Files//JetBrains//IntelliJ IDEA//bin//idea64.exe", appData);
        return 0;

        static void Check(string exeName, string exePath, string appData)
        {
            if (VsCodeAdapter.Matches(exePath))
            {
                bool hit = VsCodeAdapter.TryRead(
                    exePath, appData, out List<AdapterEntry> entries, out string reason);
                Console.WriteLine($"[{exeName}] hit={hit} count={entries.Count} reason={reason}");
                foreach (AdapterEntry entry in entries.Take(3))
                {
                    Console.WriteLine($"  {entry.Keys} = {entry.Action}");
                }

                return;
            }

            bool jbHit = JetBrainsAdapter.TryRead(
                exePath, appData, out List<AdapterEntry> jbEntries, out string jbReason, out string product);
            Console.WriteLine(
                $"[{exeName}] hit={jbHit} count={jbEntries.Count} product={product} reason={jbReason}");
            foreach (AdapterEntry entry in jbEntries.Take(3))
            {
                Console.WriteLine($"  {entry.Keys} = {entry.Action}");
            }
        }
    }

    /// <summary>
    /// 设置页自检：打开真实档案，打印打开前后的私有工作集，10 秒后自动关闭。
    /// 验收「打开时 ≤ 8 MB 增量」用它取证。
    /// </summary>
    private static int RunSettingsDemo()
    {
        ApplicationConfiguration.Initialize();

        var repository = new ShortcutRepository(
            Path.Combine(AppContext.BaseDirectory, "Data", "apps"),
            HudApp.DefaultUserAppsDirectory);

        var process = Process.GetCurrentProcess();
        process.Refresh();
        long before = process.PrivateMemorySize64;

        var form = new SettingsForm(repository, () => { });
        form.Shown += (_, _) =>
        {
            process.Refresh();
            long after = process.PrivateMemorySize64;
            Console.WriteLine($"[settings] before={before / 1024} KB after={after / 1024} KB "
                              + $"delta={(after - before) / 1024} KB");
        };

        if (Environment.GetEnvironmentVariable("HOTKEYHUD_SETTINGS_EDIT") == "1")
        {
            var edit = new System.Windows.Forms.Timer { Interval = 1500 };
            edit.Tick += (_, _) =>
            {
                edit.Stop();
                form.DemoEdit(0, 1); // 第一行 + 蓝色，展示编辑态
            };
            edit.Start();
        }

        var timer = new System.Windows.Forms.Timer { Interval = 10_000 };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            form.Close();
        };
        timer.Start();

        Application.Run(form);
        process.Refresh();
        Console.WriteLine($"[settings] after close={process.PrivateMemorySize64 / 1024} KB");
        return 0;
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
        bool SuppressionTest, bool ShellTest, bool S6Test, bool HookProbe, bool MakeIcon,
        bool PagingTest, bool ProbeSingleInstance, bool Diag, bool AdapterCheck, string? ScreenShot, string? TemplatePreview, bool SettingsDemo,
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
        bool hookProbe = false;
        bool makeIcon = false;
        bool pagingTest = false;
        bool probeSingleInstance = false;
        bool diag = false;
        bool adapterCheck = false;
        string? screenShot = null;
        string? templatePreview = null;
        bool settingsDemo = false;
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

                case "--paging-test":
                    RequireDiagnosticsBuild(arg);
                    pagingTest = true;
                    break;

                case "--make-icon":
                    RequireDiagnosticsBuild(arg);
                    makeIcon = true;
                    break;

                case "--hook-probe":
                    RequireDiagnosticsBuild(arg);
                    hookProbe = true;
                    break;

                case "--probe-single-instance":
                    RequireDiagnosticsBuild(arg);
                    probeSingleInstance = true;
                    break;

                case "--settings-demo":
                    RequireDiagnosticsBuild(arg);
                    settingsDemo = true;
                    break;

                case "--adapter-check":
                    RequireDiagnosticsBuild(arg);
                    adapterCheck = true;
                    break;

                case "--diag":
                    RequireDiagnosticsBuild(arg);
                    diag = true;
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
                    else if (TryReadValue(arg, "--template-preview", out string? previewPath))
                    {
                        RequireDiagnosticsBuild(arg);
                        if (string.IsNullOrWhiteSpace(previewPath))
                        {
                            throw new ArgumentException("--template-preview 需要 exe 路径");
                        }

                        templatePreview = previewPath;
                    }
                    else if (TryReadValue(arg, "--screen-shot", out string? shotPath))
                    {
                        RequireDiagnosticsBuild(arg);
                        if (string.IsNullOrWhiteSpace(shotPath))
                        {
                            throw new ArgumentException("--screen-shot 需要非空路径");
                        }

                        screenShot = shotPath;
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
                            + "--render-gallery --suppression-test --shell-test --s6-test --hook-probe --make-icon --paging-test --diag --adapter-check --screen-shot=PATH --template-preview=EXE --settings-demo --demo "
                            + "--seconds=N --capture=PATH --out=DIR"
                            + "（注意值必须用 = 连接）");
                    }

                    break;
            }
        }

        return new Options(selfTest, typingTest, integrationTest, renderGallery, suppressionTest,
            shellTest, s6Test, hookProbe, makeIcon, pagingTest, probeSingleInstance, diag, adapterCheck, screenShot, templatePreview, settingsDemo, demo, seconds, capture, output);
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
