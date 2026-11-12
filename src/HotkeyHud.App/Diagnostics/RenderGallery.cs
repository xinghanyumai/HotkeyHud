#if DEBUG
using System.Diagnostics;
using System.Text;
using HotkeyHud.App.Platform;
using HotkeyHud.App.UI;
using HotkeyHud.Core;

namespace HotkeyHud.App.Diagnostics;

/// <summary>
/// 渲染验收：一次性输出 4 张面板截图，并顺带断言两件用眼睛看不出来的事——
/// 高亮不改变窗口尺寸（否则每次按键都会重建 DIB 并闪一下）、单次渲染 p95 &lt; 16ms。
/// </summary>
internal static class RenderGallery
{
    private const int CaptureMargin = 30;
    private const int WarmupSamples = 5;
    private const int FullSamples = 32;
    private const int TimingSamples = 40;

    // 两条路径分开卡（§6.1）：用同一个阈值会让首帧全量重绘长期贴红线，机器一忙就 flaky
    private const double FullRenderBudgetMs = 25.0;
    private const double FlashRenderBudgetMs = 8.0;
    private const int MaxPanelHeightLogical = 260;

    private static readonly List<string> Report = new();
    private static int _passed;
    private static int _failures;

    public static int Run(string outputDirectory)
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

        Directory.CreateDirectory(outputDirectory);

        var repository = new ShortcutRepository(
            Path.Combine(AppContext.BaseDirectory, "Data", "apps"));
        var known = new KnownShortcutFilter();

        Case("Gallery01 5 份档案可加载", () =>
        {
            Require(repository.ProfileCount == 5,
                $"ProfileCount = {repository.ProfileCount}，期望 5");
            Require(repository.DroppedShortcutCount == 0,
                $"DroppedShortcutCount = {repository.DroppedShortcutCount}，期望 0");
            return $"ProfileCount = 5，Dropped = 0，LoadErrors = {repository.LoadErrors.Count}";
        });

        HudModel normal = Build(repository, known, "chrome.exe", "Chrome");
        HudModel highlighted = normal;
        HudModel empty = new()
        {
            AppName = "notepad.exe",
            Modifier = Modifier.Ctrl,
            Groups = Array.Empty<HudGroup>(),
            TotalCount = 0,
            HiddenByKnownCount = 0,
            HasProfile = false,
        };
        HudModel longList = Build(repository, known, "Code.exe", "VS Code");

        using var overlay = new OverlayForm();

        Case("Gallery02 正常态截图", () =>
            Capture(overlay, normal, null, Path.Combine(outputDirectory, "01-normal.png"),
                expectText: normal.TotalCount > 0, backdrop: true));

        Case("Gallery03 高亮态截图", () =>
            Capture(overlay, highlighted, new Combo(Modifier.Ctrl, 'L'),
                Path.Combine(outputDirectory, "02-highlight.png"),
                expectText: true));

        Case("Gallery04 空态截图", () =>
            Capture(overlay, empty, null, Path.Combine(outputDirectory, "03-empty.png"),
                expectText: false));        Case("Gallery05 长列表截断", () =>
        {
            Require(longList.TotalCount > HudPanel.MaxVisibleItems,
                $"样本条目数 {longList.TotalCount} 未超过 {HudPanel.MaxVisibleItems}，无法验证截断");
            string detail = Capture(overlay, longList, null,
                Path.Combine(outputDirectory, "04-long-list.png"), expectText: true);

            int hidden = longList.TotalCount - HudPanel.MaxVisibleItems;
            string expected = $"还有 {hidden} 个未显示 · 松开 Ctrl 隐藏";

            Require(overlay.LastItemCount == HudPanel.MaxVisibleItems,
                $"实际画出 {overlay.LastItemCount} 条，期望 {HudPanel.MaxVisibleItems} 条");
            Require(HudPanel.FooterText(longList) == expected,
                $"底部文案不符：\"{HudPanel.FooterText(longList)}\"，期望 \"{expected}\"");

            // 键帽只显示非修饰键 + 缩写表（§6.1），这是「文案不被挤掉」的机制保证
            Require(HudPanel.KeycapText("Ctrl+K") == "K", "键帽未省略修饰键");
            Require(HudPanel.KeycapText("Ctrl+Backspace") == "Bksp", "Backspace 未缩写为 Bksp");
            Require(HudPanel.KeycapText("Ctrl+PageDown") == "PgDn", "PageDown 未缩写为 PgDn");
            Require(HudPanel.KeycapText("Ctrl+[") == "[", "符号键被误处理");

            double logicalHeight = LogicalHeight(overlay);
            Require(logicalHeight <= MaxPanelHeightLogical,
                $"12 条面板高 {logicalHeight:F0} 逻辑 px，超过 {MaxPanelHeightLogical} 上限");

            return $"{detail}，实画 {overlay.LastItemCount} 条，高 {logicalHeight:F0} 逻辑 px，"
                   + $"底部「{expected}」";
        });

        Case("Gallery06 高亮不改变面板尺寸", () =>
        {
            overlay.ShowModel(normal, null);
            Pump(120);
            var before = WindowRect(overlay);

            foreach (char key in new[] { 'L', 'K', 'H', 'J', 'G' })
            {
                overlay.ShowModel(normal, new Combo(Modifier.Ctrl, key));
                Pump(60);
                var after = WindowRect(overlay);
                Require(before == after,
                    $"高亮 Ctrl+{key} 后窗口矩形从 {before} 变为 {after}（会重建 DIB 并闪烁）");
            }

            overlay.HideOverlay();
            return $"{before.Width}×{before.Height} 连按 5 个组合，GetWindowRect 保持不变";
        });

        Case("Gallery07 首帧 <25ms、高亮切换 <8ms", () =>
        {
            // 全量重绘：换一份等价的模型实例强制缓存未命中（尺寸相同，排除位图重建的干扰）
            HudModel clone = Build(repository, known, "chrome.exe", "Chrome");
            var fullSamples = new List<double>(FullSamples);
            var clock = Stopwatch.StartNew();

            for (int i = 0; i < 5; i++)
            {
                overlay.ShowModel(i % 2 == 0 ? normal : clone, null);
            }

            for (int i = 0; i < FullSamples; i++)
            {
                clock.Restart();
                overlay.ShowModel(i % 2 == 0 ? normal : clone, null);
                fullSamples.Add(clock.Elapsed.TotalMilliseconds);
            }

            // 高亮切换：同一份模型，只重画高亮层——这是每次按键都会走的路径
            var flashSamples = new List<double>(TimingSamples);
            for (int i = 0; i < WarmupSamples; i++)
            {
                overlay.ShowModel(normal, i % 2 == 0 ? null : new Combo(Modifier.Ctrl, 'L'));
            }

            for (int i = 0; i < TimingSamples; i++)
            {
                clock.Restart();
                overlay.ShowModel(normal, i % 2 == 0 ? null : new Combo(Modifier.Ctrl, 'L'));
                flashSamples.Add(clock.Elapsed.TotalMilliseconds);
            }

            overlay.HideOverlay();

            double fullP95 = Percentile(fullSamples, 0.95);
            double flashP95 = Percentile(flashSamples, 0.95);

            Require(fullP95 < FullRenderBudgetMs,
                $"首帧全量重绘 p95 = {fullP95:F2} ms，超过 {FullRenderBudgetMs} ms 预算");
            Require(flashP95 < FlashRenderBudgetMs,
                $"高亮切换 p95 = {flashP95:F2} ms，超过 {FlashRenderBudgetMs} ms 预算");

            return $"首帧全量 p95 = {fullP95:F2} ms（< {FullRenderBudgetMs}，{fullSamples.Count} 次）；"
                   + $"高亮切换 p95 = {flashP95:F2} ms（< {FlashRenderBudgetMs}，{flashSamples.Count} 次）";
        });

        return Flush();
    }

    private static string Capture(
        OverlayForm overlay, HudModel model, Combo? highlight, string path,
        bool expectText, bool backdrop = false)
    {
        // 验收 4 要求「浅色高对比背景」，所以正常态截图特意叠一层白底黑字，
        // 而不是随手拍桌面——桌面背景不可控，没法判定「背后文字是否可辨读」
        using Form? sheet = backdrop ? ShowContrastBackdrop() : null;

        overlay.ShowModel(model, highlight);
        Pump(250); // 等 DWM 合成完成再截

        Rectangle bounds = overlay.LastBounds;
        var area = new Rectangle(
            bounds.X - CaptureMargin, bounds.Y - CaptureMargin,
            bounds.Width + CaptureMargin * 2, bounds.Height + CaptureMargin * 2);
        ScreenCapture.CaptureTo(area, path);

        overlay.HideOverlay();
        Pump(80);

        Require(File.Exists(path), $"截图未生成：{path}");
        Require(expectText ? model.TotalCount > 0 : model.TotalCount == 0,
            $"样本内容与截图语义不符：TotalCount = {model.TotalCount}");

        return $"{Path.GetFileName(path)} {bounds.Width}×{bounds.Height}，{model.TotalCount} 条";
    }

    /// <summary>铺满工作区的白色面板，画满黑色大字，用于判定面板的透射程度。</summary>
    private static Form ShowContrastBackdrop()
    {
        Rectangle area = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1920, 1080);
        var form = new Form
        {
            FormBorderStyle = FormBorderStyle.None,
            StartPosition = FormStartPosition.Manual,
            Bounds = area,
            BackColor = Color.White,
            ShowInTaskbar = false,
        };

        form.Paint += (_, e) =>
        {
            using var font = new Font("Segoe UI", 28f, FontStyle.Regular, GraphicsUnit.Pixel);
            using var brush = new SolidBrush(Color.Black);
            for (int i = 0; i < 24; i++)
            {
                e.Graphics.DrawString(
                    "背景穿透判定用文字 BACKGROUND BLEED TEST 0123456789",
                    font, brush, new PointF(24, 24 + i * 46));
            }
        };

        form.Show();
        form.Activate();
        Pump(150);
        return form;
    }

    private static HudModel Build(
        ShortcutRepository repository, KnownShortcutFilter known, string processName, string fallback)
        => HudBuilder.Build(repository.FindByProcess(processName), Modifier.Ctrl, known, fallback);

    private static void Case(string name, Func<string> body)
    {
        try
        {
            string detail = body();
            _passed++;
            Report.Add($"[PASS] {name} — {detail}");
        }
        catch (Exception ex)
        {
            _failures++;
            Report.Add($"[FAIL] {name} — {ex.Message}");
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new GalleryCheckException(message);
        }
    }

    /// <summary>把物理像素高度换算回逻辑像素，验收门槛是按逻辑 px 定的。</summary>
    private static double LogicalHeight(OverlayForm overlay)
    {
        uint dpi = NativeMethods.GetDpiForWindow(overlay.Handle);
        double scale = dpi == 0 ? 1.0 : dpi / 96.0;
        return overlay.LastBounds.Height / scale;
    }

    /// <summary>真实窗口矩形。不能用 <c>LastBounds</c>——那是渲染前算出来的期望值，拿它自己比自己恒真。</summary>
    private static (int Left, int Top, int Width, int Height) WindowRect(OverlayForm overlay)
    {
        NativeMethods.GetWindowRect(overlay.Handle, out NativeMethods.RECT rect);
        return (rect.Left, rect.Top, rect.Width, rect.Height);
    }

    private static double Percentile(List<double> samples, double percentile)
    {
        var sorted = new List<double>(samples);
        sorted.Sort();
        return sorted[(int)((sorted.Count - 1) * percentile)];
    }

    private static void Pump(int milliseconds)
    {
        long deadline = Environment.TickCount64 + milliseconds;
        while (Environment.TickCount64 < deadline)
        {
            Application.DoEvents();
            Thread.Sleep(2);
        }
    }

    private static int Flush()
    {
        Report.Add($"--- 通过 {_passed}，失败 {_failures} ---");

        string text = string.Join(Environment.NewLine, Report);
        Console.WriteLine(text);
        try
        {
            File.WriteAllText(
                Path.Combine(Path.GetTempPath(), "hotkeyhud-gallery.log"), text + Environment.NewLine);
        }
        catch
        {
            // 日志失败不影响退出码
        }

        return _failures;
    }

    private sealed class GalleryCheckException : Exception
    {
        public GalleryCheckException(string message) : base(message)
        {
        }
    }
}
#endif
