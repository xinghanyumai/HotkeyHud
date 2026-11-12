namespace HotkeyHud.Core;

/// <summary>档案里的一条快捷键。<see cref="IsValid"/> 为 false 表示 keys 解析失败，应被丢弃。</summary>
public sealed class ShortcutEntry
{
    public string Keys { get; init; } = string.Empty;

    public string Action { get; init; } = string.Empty;

    public string? Group { get; init; }

    public Combo Combo { get; init; }

    public bool IsValid { get; init; }
}

public sealed class AppShortcutProfile
{
    public string App { get; init; } = string.Empty;

    public string DisplayName { get; init; } = string.Empty;

    public string[] ProcessNames { get; init; } = Array.Empty<string>();

    public List<ShortcutEntry> Shortcuts { get; init; } = new();
}

/// <summary>面板上的一行：键 + 效果文案。</summary>
public sealed class HudItem
{
    public string Keys { get; init; } = string.Empty;

    public string Action { get; init; } = string.Empty;
}

public sealed class HudGroup
{
    public string Title { get; init; } = string.Empty;

    public IReadOnlyList<HudItem> Items { get; init; } = Array.Empty<HudItem>();
}

/// <summary>UI 层只消费这个模型，不再做任何过滤、排序或补全（§6）。</summary>
public sealed class HudModel
{
    public string AppName { get; init; } = string.Empty;

    public Modifier Modifier { get; init; }

    public IReadOnlyList<HudGroup> Groups { get; init; } = Array.Empty<HudGroup>();

    public int TotalCount { get; init; }

    public int HiddenByKnownCount { get; init; }

    public bool HasProfile { get; init; }
}
