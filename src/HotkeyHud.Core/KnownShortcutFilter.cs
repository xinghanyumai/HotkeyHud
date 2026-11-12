namespace HotkeyHud.Core;

/// <summary>
/// §4 熟记名单。既用于列表过滤（<see cref="IsKnown(Combo)"/>），也用于闪烁抑制
/// （由 <see cref="TriggerStateMachine.FlashPredicate"/> 接上），两个作用点缺一不可。
/// </summary>
public sealed class KnownShortcutFilter
{
    /// <summary>§4.2 默认名单。语义上等价于「用户已形成肌肉记忆、不需要看到」的组合。</summary>
    public static IReadOnlyList<string> DefaultCombos { get; } = new[]
    {
        // 剪贴板
        "Ctrl+C", "Ctrl+V", "Ctrl+X",
        // 撤销重做
        "Ctrl+Z", "Ctrl+Y", "Ctrl+Shift+Z",
        // 全选保存
        "Ctrl+A", "Ctrl+S", "Ctrl+Shift+S",
        // 查找打印
        "Ctrl+F", "Ctrl+P",
        // 新建打开关闭
        "Ctrl+N", "Ctrl+O", "Ctrl+W", "Ctrl+T", "Ctrl+Shift+N", "Ctrl+Shift+T",
        // 格式
        "Ctrl+B", "Ctrl+I", "Ctrl+U",
        // 窗口
        "Alt+Tab", "Alt+F4", "Ctrl+Tab", "Ctrl+F4",
        // 系统
        "Win+D", "Win+E", "Win+L", "Win+R", "Win+Tab",
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

    /// <summary>
    /// 直接接给 <see cref="TriggerStateMachine.FlashPredicate"/> 用。
    /// 提供这个入口是为了让 §4.3 的第二个作用点（闪烁抑制）不会被接线时漏掉。
    /// </summary>
    public bool AllowFlash(Combo combo) => !IsKnown(combo);
}
