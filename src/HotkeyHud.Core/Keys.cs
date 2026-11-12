namespace HotkeyHud.Core;

[Flags]
public enum Modifier
{
    None = 0,
    Ctrl = 1,
    Alt = 2,
    Shift = 4,
    Win = 8,
}

/// <summary>修饰键 + 单键。VirtualKey 为 0 表示纯修饰键组合。</summary>
public readonly record struct Combo(Modifier Modifiers, int VirtualKey)
{
    public override string ToString() => Keys.Format(this);
}

/// <summary>一次按键事件。时间戳由调用方提供，Core 内不存在任何时钟。</summary>
public readonly record struct KeyEvent(int VirtualKey, bool IsDown, long TimestampMs);

public static class Keys
{
    public const int VkShift = 0x10;
    public const int VkControl = 0x11;
    public const int VkMenu = 0x12;
    public const int VkLShift = 0xA0;
    public const int VkRShift = 0xA1;
    public const int VkLControl = 0xA2;
    public const int VkRControl = 0xA3;
    public const int VkLMenu = 0xA4;
    public const int VkRMenu = 0xA5;
    public const int VkLWin = 0x5B;
    public const int VkRWin = 0x5C;

    /// <summary>VK → 修饰键。左右 Ctrl/Alt/Shift 归一到同一个修饰键（坑 #4）。</summary>
    public static Modifier ModifierOf(int virtualKey) => virtualKey switch
    {
        VkControl or VkLControl or VkRControl => Modifier.Ctrl,
        VkMenu or VkLMenu or VkRMenu => Modifier.Alt,
        VkShift or VkLShift or VkRShift => Modifier.Shift,
        VkLWin or VkRWin => Modifier.Win,
        _ => Modifier.None,
    };

    public static bool IsModifier(int virtualKey) => ModifierOf(virtualKey) != Modifier.None;

    /// <summary>
    /// 解析「修饰键 + 单键」形式的组合文本，例如 <c>Ctrl+K</c>、<c>Ctrl+Shift+=</c>。
    /// 解析失败返回 false，调用方负责丢弃并计数（§3.1）。
    /// </summary>
    public static bool TryParse(string? text, out Combo combo)
    {
        combo = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        string[] parts = text.Split('+', StringSplitOptions.TrimEntries);
        if (parts.Any(part => part.Length == 0))
        {
            return false; // 例如 "Ctrl++" 这类畸形写法
        }

        Modifier modifiers = Modifier.None;
        for (int i = 0; i < parts.Length - 1; i++)
        {
            if (!TryParseModifier(parts[i], out Modifier modifier))
            {
                return false;
            }

            modifiers |= modifier;
        }

        if (!TryParseKey(parts[^1], out int virtualKey))
        {
            return false;
        }

        combo = new Combo(modifiers, virtualKey);
        return true;
    }

    public static string Format(Combo combo)
    {
        var parts = new List<string>(5);
        if ((combo.Modifiers & Modifier.Ctrl) != 0)
        {
            parts.Add("Ctrl");
        }

        if ((combo.Modifiers & Modifier.Alt) != 0)
        {
            parts.Add("Alt");
        }

        if ((combo.Modifiers & Modifier.Shift) != 0)
        {
            parts.Add("Shift");
        }

        if ((combo.Modifiers & Modifier.Win) != 0)
        {
            parts.Add("Win");
        }

        if (combo.VirtualKey != 0)
        {
            parts.Add(KeyName(combo.VirtualKey));
        }

        return string.Join('+', parts);
    }

    public static string KeyName(int virtualKey)
    {
        if (virtualKey is >= 'A' and <= 'Z' || virtualKey is >= '0' and <= '9')
        {
            return ((char)virtualKey).ToString();
        }

        if (virtualKey is >= 0x70 and <= 0x87)
        {
            return "F" + (virtualKey - 0x70 + 1);
        }

        foreach ((char ch, int vk) in PunctuationKeys)
        {
            if (vk == virtualKey)
            {
                return ch.ToString();
            }
        }

        foreach ((string name, int vk) in NamedKeys)
        {
            if (vk == virtualKey)
            {
                return name;
            }
        }

        return $"0x{virtualKey:X2}";
    }

    private static bool TryParseModifier(string text, out Modifier modifier)
    {
        switch (text.ToLowerInvariant())
        {
            case "ctrl":
            case "control":
                modifier = Modifier.Ctrl;
                return true;
            case "alt":
                modifier = Modifier.Alt;
                return true;
            case "shift":
                modifier = Modifier.Shift;
                return true;
            case "win":
            case "windows":
            case "meta":
                modifier = Modifier.Win;
                return true;
            default:
                modifier = Modifier.None;
                return false;
        }
    }

    private static bool TryParseKey(string text, out int virtualKey)
    {
        virtualKey = 0;

        if (text.Length == 1)
        {
            char upper = char.ToUpperInvariant(text[0]);
            if (upper is >= 'A' and <= 'Z' || upper is >= '0' and <= '9')
            {
                virtualKey = upper;
                return true;
            }

            foreach ((char ch, int vk) in PunctuationKeys)
            {
                if (ch == text[0])
                {
                    virtualKey = vk;
                    return true;
                }
            }

            return false;
        }

        if (text.Length is 2 or 3
            && (text[0] is 'F' or 'f')
            && int.TryParse(text[1..], out int functionNumber)
            && functionNumber is >= 1 and <= 24)
        {
            virtualKey = 0x70 + functionNumber - 1;
            return true;
        }

        foreach ((string name, int vk) in NamedKeys)
        {
            if (string.Equals(name, text, StringComparison.OrdinalIgnoreCase))
            {
                virtualKey = vk;
                return true;
            }
        }

        return false;
    }

    // 与 US 布局对齐；本项目不处理非 US 布局的 OEM 键差异
    private static readonly (char Character, int VirtualKey)[] PunctuationKeys =
    {
        (';', 0xBA),
        ('=', 0xBB),
        (',', 0xBC),
        ('-', 0xBD),
        ('.', 0xBE),
        ('/', 0xBF),
        ('`', 0xC0),
        ('[', 0xDB),
        ('\\', 0xDC),
        (']', 0xDD),
        ('\'', 0xDE),
    };

    private static readonly (string Name, int VirtualKey)[] NamedKeys =
    {
        ("Space", 0x20),
        ("Enter", 0x0D),
        ("Tab", 0x09),
        ("Esc", 0x1B),
        ("Backspace", 0x08),
        ("Delete", 0x2E),
        ("Insert", 0x2D),
        ("Home", 0x24),
        ("End", 0x23),
        ("PageUp", 0x21),
        ("PageDown", 0x22),
        ("Left", 0x25),
        ("Up", 0x26),
        ("Right", 0x27),
        ("Down", 0x28),
        ("PrintScreen", 0x2C),
        ("Pause", 0x13),
        ("CapsLock", 0x14),
        ("NumLock", 0x90),
        ("ScrollLock", 0x91),
        ("Apps", 0x5D),
        ("Sleep", 0x5F),
    };
}
