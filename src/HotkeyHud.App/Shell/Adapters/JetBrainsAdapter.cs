using System.Xml.Linq;

namespace HotkeyHud.App.Shell.Adapters;

/// <summary>
/// JetBrains 适配器（§3.4.2）：读 <c>%APPDATA%\JetBrains\&lt;product&gt;\keymaps\*.xml</c>。
/// <para>
/// 只取 <c>first-keystroke</c>；带 <c>second-keystroke</c> 的两段式组合本产品表达不了，跳过。
/// <c>action id</c> 直接当动作文案——不翻译。
/// </para>
/// </summary>
internal static class JetBrainsAdapter
{
    public const string GroupName = "JetBrains 自定义";

    /// <summary>JetBrains 各 IDE 的可执行文件名。不在名单里的一律不猜。</summary>
    private static readonly HashSet<string> ProcessNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "idea64.exe", "pycharm64.exe", "webstorm64.exe", "clion64.exe", "goland64.exe",
        "rider64.exe", "phpstorm64.exe", "datagrip64.exe", "rubymine64.exe", "aqua64.exe",
        "rustrover64.exe", "fleet.exe",
    };

    public static bool Matches(string exePath)
        => ProcessNames.Contains(Path.GetFileName(exePath));

    public static bool TryRead(
        string exePath, string appDataRoot, out List<AdapterEntry> entries, out string reason, out string product)
    {
        entries = new List<AdapterEntry>();
        product = string.Empty;

        if (!Matches(exePath))
        {
            reason = "不是 JetBrains IDE 的可执行文件";
            return false;
        }

        string root = Path.Combine(appDataRoot, "JetBrains");
        if (!Directory.Exists(root))
        {
            reason = $"没找到 {root}";
            return false;
        }

        // 多产品目录：取最近写过的那个，并在提示里说明选了哪个
        string? newest = Directory.EnumerateDirectories(root)
            .Where(dir => Directory.Exists(Path.Combine(dir, "keymaps")))
            .OrderByDescending(Directory.GetLastWriteTimeUtc)
            .FirstOrDefault();

        if (newest is null)
        {
            reason = "JetBrains 目录下没有带 keymaps 的产品目录";
            return false;
        }

        product = Path.GetFileName(newest);
        string keymaps = Path.Combine(newest, "keymaps");

        string? file = Directory.EnumerateFiles(keymaps, "*.xml")
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();

        if (file is null)
        {
            reason = $"{product}\\keymaps 下没有 xml（用的是默认键位）";
            return false;
        }

        try
        {
            XDocument document = XDocument.Load(file);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (XElement action in document.Descendants("action"))
            {
                string? id = action.Attribute("id")?.Value;
                if (string.IsNullOrWhiteSpace(id))
                {
                    continue;
                }

                foreach (XElement shortcut in action.Elements("keyboard-shortcut"))
                {
                    // 两段式（second-keystroke）跳过：HUD 只表达单组合
                    if (shortcut.Attribute("second-keystroke") is not null)
                    {
                        continue;
                    }

                    string? stroke = shortcut.Attribute("first-keystroke")?.Value;
                    if (string.IsNullOrWhiteSpace(stroke))
                    {
                        continue;
                    }

                    if (!TryConvertStroke(stroke, out string keys) || !seen.Add(keys))
                    {
                        continue;
                    }

                    entries.Add(new AdapterEntry(keys, id));
                    break; // 一个 action 只取第一条可用组合
                }
            }

            reason = entries.Count == 0 ? $"{Path.GetFileName(file)} 里没有可用键位" : string.Empty;
            return true;
        }
        catch (Exception ex) when (ex is System.Xml.XmlException or IOException or UnauthorizedAccessException)
        {
            reason = $"解析失败：{ex.Message}";
            return false;
        }
    }

    /// <summary><c>control shift S</c> → <c>Ctrl+Shift+S</c>。</summary>
    internal static bool TryConvertStroke(string stroke, out string result)
    {
        result = string.Empty;

        string[] parts = stroke.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            return false;
        }

        var modifiers = new List<string>(3);
        for (int i = 0; i < parts.Length - 1; i++)
        {
            switch (parts[i].ToLowerInvariant())
            {
                case "control":
                    modifiers.Add("Ctrl");
                    break;
                case "shift":
                    modifiers.Add("Shift");
                    break;
                case "alt":
                    modifiers.Add("Alt");
                    break;
                case "meta":
                    modifiers.Add("Win");
                    break;
                default:
                    return false;
            }
        }

        if (!TryKeyName(parts[^1], out string keyName))
        {
            return false;
        }

        modifiers.Add(keyName);
        result = string.Join('+', modifiers);
        return true;
    }

    private static bool TryKeyName(string raw, out string name)
    {
        name = string.Empty;
        string text = raw.Trim();
        if (text.Length == 0)
        {
            return false;
        }

        if (text.Length == 1)
        {
            char c = text[0];
            if (char.IsLetterOrDigit(c) || "`-=[]\\;',./".Contains(c, StringComparison.Ordinal))
            {
                name = char.ToUpperInvariant(c).ToString();
                return true;
            }

            return false;
        }

        if (text[0] is 'F' or 'f' && int.TryParse(text[1..], out int fn) && fn is >= 1 and <= 24)
        {
            name = "F" + fn;
            return true;
        }

        switch (text.ToUpperInvariant())
        {
            case "ENTER": name = "Enter"; return true;
            case "TAB": name = "Tab"; return true;
            case "ESCAPE": name = "Esc"; return true;
            case "SPACE": name = "Space"; return true;
            case "BACK_SPACE": name = "Backspace"; return true;
            case "DELETE": name = "Delete"; return true;
            case "INSERT": name = "Insert"; return true;
            case "HOME": name = "Home"; return true;
            case "END": name = "End"; return true;
            case "PAGE_UP": name = "PageUp"; return true;
            case "PAGE_DOWN": name = "PageDown"; return true;
            case "UP": name = "Up"; return true;
            case "DOWN": name = "Down"; return true;
            case "LEFT": name = "Left"; return true;
            case "RIGHT": name = "Right"; return true;
            case "COMMA": name = ","; return true;
            case "PERIOD": name = "."; return true;
            case "SLASH": name = "/"; return true;
            case "BACK_SLASH": name = "\\"; return true;
            case "SEMICOLON": name = ";"; return true;
            case "EQUALS": name = "="; return true;
            case "MINUS": name = "-"; return true;
            case "OPEN_BRACKET": name = "["; return true;
            case "CLOSE_BRACKET": name = "]"; return true;
            case "BACK_QUOTE": name = "`"; return true;
            case "QUOTE": name = "'"; return true;
            default: return false;
        }
    }
}
