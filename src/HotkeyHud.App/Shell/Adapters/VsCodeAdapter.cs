using System.Text.Json;

namespace HotkeyHud.App.Shell.Adapters;

/// <summary>
/// VS Code 适配器（§3.4.2）：读 <c>%APPDATA%\Code\User\keybindings.json</c>。
/// <para>
/// 只能读到**用户改过或新增**的键位——应用内置默认键位在安装目录的只读资源里，读不到。
/// 用户从没改过时文件是空数组，产出 0 条是正确行为，不是失败。
/// </para>
/// <para>不翻译 command id：用户认识自己设的命令，硬翻成中文反而对不上。</para>
/// </summary>
internal static class VsCodeAdapter
{
    public const string GroupName = "VS Code 自定义";

    /// <summary>只认 Code.exe；其他 exe 一律不猜路径。</summary>
    public static bool Matches(string exePath)
        => string.Equals(Path.GetFileName(exePath), "Code.exe", StringComparison.OrdinalIgnoreCase);

    public static bool TryRead(
        string exePath, string appDataRoot, out List<AdapterEntry> entries, out string reason)
    {
        entries = new List<AdapterEntry>();

        if (!Matches(exePath))
        {
            reason = "不是 Code.exe";
            return false;
        }

        string file = Path.Combine(appDataRoot, "Code", "User", "keybindings.json");
        if (!File.Exists(file))
        {
            reason = $"没找到 {file}（VS Code 没装或没登录过）";
            return false;
        }

        try
        {
            string json = File.ReadAllText(file);
            using JsonDocument document = JsonDocument.Parse(
                json, new JsonDocumentOptions
                {
                    CommentHandling = JsonCommentHandling.Skip,
                    AllowTrailingCommas = true,
                });

            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                reason = "keybindings.json 顶层不是数组";
                return false;
            }

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (JsonElement item in document.RootElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                if (!item.TryGetProperty("key", out JsonElement keyElement)
                    || !item.TryGetProperty("command", out JsonElement commandElement))
                {
                    continue;
                }

                string? key = keyElement.GetString();
                string? command = commandElement.GetString();
                if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(command))
                {
                    continue;
                }

                // command 以 "-" 开头表示解绑，不是快捷键
                if (command.StartsWith('-'))
                {
                    continue;
                }

                // 多键序列（"ctrl+k ctrl+s"）本产品表达不了，跳过
                if (!TryConvertKey(key, out string keys))
                {
                    continue;
                }

                if (!seen.Add(keys))
                {
                    continue;
                }

                entries.Add(new AdapterEntry(keys, LastSegment(command)));
            }

            reason = entries.Count == 0
                ? "keybindings.json 里没有可用的单组合键位（没改过键位时这是正常的）"
                : string.Empty;
            return true;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            reason = $"读取失败：{ex.Message}";
            return false;
        }
    }

    private static string LastSegment(string command)
    {
        int index = command.LastIndexOf('.');
        return index >= 0 && index + 1 < command.Length ? command[(index + 1)..] : command;
    }

    /// <summary><c>ctrl+alt+f</c> → <c>Ctrl+Alt+F</c>。无法表达的组合返回 false。</summary>
    internal static bool TryConvertKey(string key, out string result)
    {
        result = string.Empty;

        // 空格分隔的是多键序列（ctrl+k ctrl+s），不支持
        if (key.Contains(' ', StringComparison.Ordinal))
        {
            return false;
        }

        string[] parts = key.Split('+', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            return false;
        }

        var modifiers = new List<string>(3);
        for (int i = 0; i < parts.Length - 1; i++)
        {
            switch (parts[i].ToLowerInvariant())
            {
                case "ctrl":
                    modifiers.Add("Ctrl");
                    break;
                case "shift":
                    modifiers.Add("Shift");
                    break;
                case "alt":
                    modifiers.Add("Alt");
                    break;
                case "cmd":
                case "meta":
                case "win":
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

        if (text[0] is 'f' or 'F' && int.TryParse(text[1..], out int fn) && fn is >= 1 and <= 24)
        {
            name = "F" + fn;
            return true;
        }

        switch (text.ToLowerInvariant())
        {
            case "escape": name = "Esc"; return true;
            case "enter": name = "Enter"; return true;
            case "tab": name = "Tab"; return true;
            case "space": name = "Space"; return true;
            case "backspace": name = "Backspace"; return true;
            case "delete": name = "Delete"; return true;
            case "insert": name = "Insert"; return true;
            case "home": name = "Home"; return true;
            case "end": name = "End"; return true;
            case "pageup": name = "PageUp"; return true;
            case "pagedown": name = "PageDown"; return true;
            case "up": name = "Up"; return true;
            case "down": name = "Down"; return true;
            case "left": name = "Left"; return true;
            case "right": name = "Right"; return true;
            default: return false;
        }
    }
}
