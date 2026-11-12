namespace HotkeyHud.Core;

/// <summary>§2.1 采集到的六个信号。S4 负责采集，本类型只承载数据。</summary>
public readonly record struct GameSignal(
    bool ExclusiveFullscreen,
    bool CoversWholeMonitor,
    bool HasCaption,
    string? ProcessName,
    string? WindowClass,
    string? ExecutablePath);

/// <summary>
/// 可配置项。只剩 D3 的用户名单——D5 的开关在 S5 删掉了：
/// 真机实测任何铺满屏幕的窗口都让 QUNS 报 2，D1 先命中，那个开关按下去没反应，
/// 暴露一个「按了没反应」的开关比没有开关更糟（§2.2 v1.3）。
/// </summary>
public sealed class GameRules
{
    public IReadOnlyCollection<string> GameProcessNames { get; init; } = Array.Empty<string>();
}

/// <summary>
/// §2.2 决策表的纯函数实现。命中即返回，规则顺序不可调换——
/// 顺序决定了 reason 文案与「宁可多抑制」的取舍。
/// </summary>
public static class GameClassifier
{
    // D2：引擎窗口类名稳定，误判率极低
    private static readonly string[] EngineWindowClasses =
    {
        "UnityWndClass",
        "UnrealWindow",
        "CryENGINE",
        "Valve001",
    };

    // D4：覆盖绝大多数发行平台。比较时忽略大小写（Windows 路径）
    private static readonly string[] PlatformPathFragments =
    {
        @"\steamapps\common\",
        @"\epic games\",
        @"\gog games\",
        @"\ubisoft game launcher\games\",
        @"\wegameapps\",
        @"\riot games\",
        @"\battle.net\",
    };

    public static bool ShouldSuppress(in GameSignal signal, GameRules? rules, out string reason)
    {
        // D1 系统自己都认为处于全屏/演示状态
        if (signal.ExclusiveFullscreen)
        {
            reason = "D1 系统报告独占全屏 / 演示模式";
            return true;
        }

        // D2 引擎窗口类
        if (!string.IsNullOrEmpty(signal.WindowClass))
        {
            foreach (string windowClass in EngineWindowClasses)
            {
                if (string.Equals(signal.WindowClass, windowClass, StringComparison.Ordinal))
                {
                    reason = $"D2 引擎窗口类 {windowClass}";
                    return true;
                }
            }
        }

        // D3 用户维护的游戏名单（兜底，用户可自行补）
        if (rules is not null && !string.IsNullOrEmpty(signal.ProcessName))
        {
            string process = NormalizeProcess(signal.ProcessName);
            foreach (string configured in rules.GameProcessNames)
            {
                if (string.IsNullOrWhiteSpace(configured))
                {
                    continue; // 配置里混进空项不应炸掉判定
                }

                if (string.Equals(process, NormalizeProcess(configured), StringComparison.OrdinalIgnoreCase))
                {
                    reason = $"D3 用户游戏名单 {configured}";
                    return true;
                }
            }
        }

        // D4 发行平台安装目录
        if (!string.IsNullOrEmpty(signal.ExecutablePath))
        {
            foreach (string fragment in PlatformPathFragments)
            {
                if (signal.ExecutablePath.Contains(fragment, StringComparison.OrdinalIgnoreCase))
                {
                    reason = $"D4 发行平台目录 {fragment.Trim('\\')}";
                    return true;
                }
            }
        }

        // D5 无边框全屏（会误伤放映 / 全屏视频 / F11，这是有意为之）。
        // 恒开：多数机器上 D1 的 QUNS=2 会先命中，它只在 QUNS 不上报全屏的组合下兜底
        if (signal.CoversWholeMonitor && !signal.HasCaption)
        {
            reason = "D5 无边框全屏";
            return true;
        }

        reason = string.Empty;
        return false;
    }

    private static string NormalizeProcess(string name)
    {
        string trimmed = name.Trim();
        return trimmed.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? trimmed[..^4] : trimmed;
    }
}
