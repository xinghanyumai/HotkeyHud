using System.Text.Json;
using System.Text.Json.Serialization;

namespace HotkeyHud.Core;

/// <summary>
/// §3 按进程名索引的应用档案仓库。<see cref="FindByProcess"/> 是查询前台应用快捷键的唯一入口，
/// 不存在「查不到就退回全局表」的路径。
/// </summary>
public sealed class ShortcutRepository
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private readonly string _directory;
    private readonly Dictionary<string, AppShortcutProfile> _byProcess =
        new(StringComparer.OrdinalIgnoreCase);

    private int _profileCount;

    public ShortcutRepository(string appsDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appsDirectory);
        _directory = appsDirectory;
        Reload();
    }

    public int ProfileCount => _profileCount;

    /// <summary>keys 解析失败被丢弃的条目数（§3.1 要求计数）。</summary>
    public int DroppedShortcutCount { get; private set; }

    public AppShortcutProfile? FindByProcess(string? processName)
    {
        string key = Normalize(processName);
        return key.Length != 0 && _byProcess.TryGetValue(key, out AppShortcutProfile? profile)
            ? profile
            : null;
    }

    public void Reload()
    {
        _byProcess.Clear();
        int dropped = 0;
        int loaded = 0;

        // 目录不存在 = 还没有任何档案，是合法状态，不是错误
        if (Directory.Exists(_directory))
        {
            foreach (string file in Directory
                         .EnumerateFiles(_directory, "*.json")
                         .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
            {
                AppShortcutProfile profile = LoadProfile(file, ref dropped);
                loaded++;

                foreach (string processName in profile.ProcessNames)
                {
                    string key = Normalize(processName);
                    if (key.Length == 0)
                    {
                        continue;
                    }

                    if (!_byProcess.TryAdd(key, profile))
                    {
                        throw new InvalidDataException(
                            $"进程名 \"{processName}\" 在多个档案中重复出现，无法确定该显示哪一份：{file}");
                    }
                }
            }
        }

        _profileCount = loaded;
        DroppedShortcutCount = dropped;
    }

    private static AppShortcutProfile LoadProfile(string file, ref int dropped)
    {
        string json = File.ReadAllText(file);
        ProfileDocument? document;
        try
        {
            document = JsonSerializer.Deserialize<ProfileDocument>(json, JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"档案 JSON 解析失败：{file}", ex);
        }

        if (document is null)
        {
            throw new InvalidDataException($"档案内容为空：{file}");
        }

        if (string.IsNullOrWhiteSpace(document.App))
        {
            throw new InvalidDataException($"档案缺少 app 字段：{file}");
        }

        if (document.ProcessNames is null || document.ProcessNames.Length == 0)
        {
            throw new InvalidDataException($"档案缺少 processNames：{file}");
        }

        var shortcuts = new List<ShortcutEntry>(document.Shortcuts?.Count ?? 0);
        foreach (ShortcutDocument item in document.Shortcuts ?? new List<ShortcutDocument>())
        {
            if (string.IsNullOrWhiteSpace(item.Keys))
            {
                throw new InvalidDataException($"快捷键条目缺少 keys：{file}");
            }

            if (string.IsNullOrWhiteSpace(item.Action))
            {
                throw new InvalidDataException($"快捷键 \"{item.Keys}\" 缺少 action：{file}");
            }

            bool parsed = Keys.TryParse(item.Keys, out Combo combo);
            if (!parsed)
            {
                dropped++; // §3.1：解析失败静默丢弃并计数
            }

            shortcuts.Add(new ShortcutEntry
            {
                Keys = item.Keys,
                Action = item.Action,
                Group = item.Group,
                Combo = combo,
                IsValid = parsed,
            });
        }

        return new AppShortcutProfile
        {
            App = document.App,
            DisplayName = string.IsNullOrWhiteSpace(document.DisplayName)
                ? document.App
                : document.DisplayName,
            ProcessNames = document.ProcessNames,
            Shortcuts = shortcuts,
        };
    }

    /// <summary>归一化：去空白、去 <c>.exe</c> 后缀。大小写由字典的比较器处理。</summary>
    private static string Normalize(string? processName)
    {
        if (string.IsNullOrWhiteSpace(processName))
        {
            return string.Empty;
        }

        string name = processName.Trim();
        return name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
    }

    private sealed class ProfileDocument
    {
        [JsonPropertyName("app")]
        public string? App { get; set; }

        [JsonPropertyName("displayName")]
        public string? DisplayName { get; set; }

        [JsonPropertyName("processNames")]
        public string[]? ProcessNames { get; set; }

        [JsonPropertyName("shortcuts")]
        public List<ShortcutDocument>? Shortcuts { get; set; }
    }

    private sealed class ShortcutDocument
    {
        [JsonPropertyName("keys")]
        public string? Keys { get; set; }

        [JsonPropertyName("action")]
        public string? Action { get; set; }

        [JsonPropertyName("group")]
        public string? Group { get; set; }
    }
}
