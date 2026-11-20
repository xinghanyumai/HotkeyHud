namespace HotkeyHud.Core;

/// <summary>
/// §4 熟记名单。v1.7 起只用于**降权**：命中的组合不剔除，而是归入
/// <see cref="HudBuilder.GeneralGroupTitle"/> 组排到最后一页，按下时照常高亮。
/// </summary>
public sealed class KnownShortcutFilter
{
    /// <summary>§4.2 默认名单。语义上等价于「用户已形成肌肉记忆、不需要看到」的组合。</summary>
    public static IReadOnlyList<string> DefaultCombos { get; } = new[]
    {
        // v1.5 收窄：只保留 Ctrl 单修饰键的高频编辑类。
        // Alt+* 与 Win+* 全部移除——用户明确想看到 Alt+F4。
        "Ctrl+C", "Ctrl+V", "Ctrl+X",
        "Ctrl+Z", "Ctrl+Y",
        "Ctrl+A", "Ctrl+S",
        "Ctrl+F", "Ctrl+P",
        "Ctrl+N", "Ctrl+O", "Ctrl+W", "Ctrl+T",
    };

    private readonly HashSet<Combo> _combos;

    /// <summary>
    /// <c>null</c> → 用默认名单；空集合 → 关闭过滤；非空 → 完全替换默认名单（不是追加）。
    /// 名单里有无法解析的条目时抛 <see cref="ArgumentException"/>：配置错误必须显式失败，
    /// 静默丢弃会让用户以为「已关掉过滤」。
    /// </summary>
    public KnownShortcutFilter(IEnumerable<string>? combos = null)
    {
        IEnumerable<string> source = combos ?? DefaultCombos;
        _combos = new HashSet<Combo>();

        foreach (string text in source)
        {
            if (!Keys.TryParse(text, out Combo combo))
            {
                throw new ArgumentException($"无法解析的熟记名单条目：\"{text}\"", nameof(combos));
            }

            _combos.Add(combo);
        }
    }

    public int Count => _combos.Count;

    public bool IsKnown(Combo combo) => _combos.Contains(combo);

    public bool IsKnown(string text)
        => Keys.TryParse(text, out Combo combo) && _combos.Contains(combo);

}
