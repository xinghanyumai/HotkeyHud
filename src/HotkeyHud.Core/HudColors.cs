namespace HotkeyHud.Core;

/// <summary>
/// 键帽预设色（§3.4 / S7.6）。**只给 8 个**——面板是深底，自由取色很容易做出看不清的组合。
/// </summary>
public static class HudColors
{
    /// <summary>「默认」= 用修饰键自己的颜色（Ctrl 蓝 / Alt 橙 / Shift 绿 / Win 青）。</summary>
    public const string Default = "默认";

    /// <summary>可选颜色：显示名 → 色值。</summary>
    public static readonly IReadOnlyList<(string Name, string Hex)> Presets = new[]
    {
        (Default, string.Empty),
        ("蓝", "#4C8DFF"),
        ("橙", "#FF9F45"),
        ("绿", "#3DDC97"),
        ("青", "#4CC9F0"),
        ("紫", "#B388FF"),
        ("粉", "#FF7EB6"),
        ("红", "#FF6B6B"),
    };

    /// <summary>把配置里的色值规范化：只接受预设值，其他一律当「默认」。</summary>
    public static string? Normalize(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex))
        {
            return null;
        }

        foreach ((string name, string value) in Presets)
        {
            if (value.Length > 0 && string.Equals(value, hex.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                return value;
            }

            if (value.Length > 0 && string.Equals(name, hex.Trim(), StringComparison.Ordinal))
            {
                return value;
            }
        }

        return null;
    }

    /// <summary>色值 → 显示名（给设置页用）。</summary>
    public static string DisplayName(string? hex)
    {
        string? normalized = Normalize(hex);
        if (normalized is null)
        {
            return Default;
        }

        foreach ((string name, string value) in Presets)
        {
            if (string.Equals(value, normalized, StringComparison.OrdinalIgnoreCase))
            {
                return name;
            }
        }

        return Default;
    }
}
