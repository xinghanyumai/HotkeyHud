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
    private const double RenderBudgetMs = 16.0;

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
                expectText: normal.TotalCount > 0));

        Case("Gallery03 高亮态截图", () =>
            Capture(overlay, highlighted, new Combo(Modifier.Ctrl, 'L'),
                Path.Combine(outputDirectory, "02-highlight.png"),
                expectText: true));

        Case("Gallery04 空态截图", () =>
            Capture(overlay, empty, null, Path.Combine(outputDirectory, "03-empty.png"),
                expectText: false));

        Case("Gallery05 长列表截断", () =>
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

            return $"{detail}，实画 {overlay.LastItemCount} 条，底部「{expected}」";
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

        Case("Gallery07 单次渲染 p95 < 16ms", () =>
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

            Require(fullP95 < RenderBudgetMs,
                $"全量重绘 p95 = {fullP95:F2} ms，超过 {RenderBudgetMs} ms 预算");
            Require(flashP95 < RenderBudgetMs,
                $"高亮切换 p95 = {flashP95:F2} ms，超过 {RenderBudgetMs} ms 预算");

            return $"全量重绘 p95 = {fullP95:F2} ms（{fullSamples.Count} 次）；"
                   + $"高亮切换 p95 = {flashP95:F2} ms（{flashSamples.Count} 次）";
        });

        return Flush();
    }

    private static string Capture(
        OverlayForm overlay, HudModel model, Combo? highlight, string path, bool expectText)
    {
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
