namespace HotkeyHud.App.Shell;

/// <summary>
/// 用户配置。**只暴露 4 个字段**：其余（flashMs / 最多 12 条 / 面板尺寸与配色）
/// 由规格 §6.1 定死，开放出去只会让用户调坏。
/// </summary>
public sealed class AppConfig
{
    public const int DefaultDwellMs = 300;
    public const int MinDwellMs = 80;
    public const int MaxDwellMs = 5000;

    /// <summary>长按阈值（毫秒）。用户真的会想调这一个。</summary>
    public int DwellMs { get; init; } = DefaultDwellMs;

    /// <summary>
    /// <c>null</c> → 用内置默认名单；<c>[]</c> → 关闭过滤；非空 → 完全替换默认名单。
    /// </summary>
    public IReadOnlyList<string>? KnownShortcuts { get; init; }

    /// <summary>D3 用户游戏名单兜底。</summary>
    public IReadOnlyList<string> ExtraGameProcesses { get; init; } = Array.Empty<string>();

    /// <summary>与注册表 HKCU\...\Run 的实际状态保持同步。</summary>
    public bool StartWithWindows { get; init; }

    public AppConfig With(bool startWithWindows) => new()
    {
        DwellMs = DwellMs,
        KnownShortcuts = KnownShortcuts,
        ExtraGameProcesses = ExtraGameProcesses,
        StartWithWindows = startWithWindows,
    };
}
