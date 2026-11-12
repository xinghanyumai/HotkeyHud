using System.Text.Json;
using System.Text.Json.Serialization;

namespace HotkeyHud.Core;

/// <summary>
/// §3 按进程名索引的应用档案仓库。<see cref="FindByProcess"/> 是查询前台应用快捷键的唯一入口，
/// 不存在「查不到就退回全局表」的路径。
/// <para>
/// 档案是用户可编辑的 JSON，一份写坏不该让整个程序起不来：加载失败的文件会被跳过，
/// 原因记入 <see cref="LoadErrors"/>，由调用方（托盘提示）决定怎么告知用户。
/// 这不是静默 fallback——错误被显式保留下来了。
/// </para>
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
    private Dictionary<string, AppShortcutProfile> _byProcess = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<string> _loadErrors = Array.Empty<string>();
    private int _profileCount;

    public ShortcutRepository(string appsDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appsDirectory);
        _directory = appsDirectory;
        Reload();
    }

    public int ProfileCount => _profileCount;

    /// <summary>最近一次 <see cref="Reload"/> 中跳过的档案及原因。空表示全部加载成功。</summary>
    public IReadOnlyList<string> LoadErrors => _loadErrors;

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
        // 先建局部索引，全部成功后再整体替换：中途失败不会留下半加载状态
        var byProcess = new Dictionary<string, AppShortcutProfile>(StringComparer.OrdinalIgnoreCase);
        var errors = new List<string>();
        int dropped = 0;
        int loaded = 0;

        // 目录不存在 = 还没有任何档案，是合法状态，不是错误
        if (Directory.Exists(_directory))
        {
            foreach (string file in Directory
                         .EnumerateFiles(_directory, "*.json")
                         .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
            {
                AppShortcutProfile? profile = TryLoadProfile(file, ref dropped, errors);
                if (profile is null)
                {
                    continue;
                }

                var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (string processName in profile.ProcessNames)
                {
                    string key = Normalize(processName);
                    if (key.Length != 0)
                    {
                        keys.Add(key);
                    }
                }

                string? conflict = keys.FirstOrDefault(byProcess.ContainsKey);
                if (conflict is not null)
                {
                    errors.Add($"{file}: 进程名 \"{conflict}\" 已由其他档案占用，整份档案跳过");
                    continue;
                }

                foreach (string key in keys)
                {
                    byProcess[key] = profile;
                }

                loaded++;
            }
        }

        _byProcess = byProcess;
        _loadErrors = errors;
        _profileCount = loaded;
        DroppedShortcutCount = dropped;
    }

    private static AppShortcutProfile? TryLoadProfile(string file, ref int dropped, List<string> errors)
    {
        ProfileDocument? document;
        try
        {
            string json = File.ReadAllText(file);
            document = JsonSerializer.Deserialize<ProfileDocument>(json, JsonOptions);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            errors.Add($"{file}: 读取或解析失败 —— {ex.Message}");
            return null;
        }

        if (document is null)
        {
            errors.Add($"{file}: 档案内容为空");
            return null;
        }

        if (string.IsNullOrWhiteSpace(document.App))
        {
            errors.Add($"{file}: 缺少 app 字段");
            return null;
        }

        if (document.ProcessNames is null || document.ProcessNames.Length == 0)
        {
            errors.Add($"{file}: 缺少 processNames");
            return null;
        }

        var shortcuts = new List<ShortcutEntry>(document.Shortcuts?.Count ?? 0);
        foreach (ShortcutDocument item in document.Shortcuts ?? new List<ShortcutDocument>())
        {
            if (string.IsNullOrWhiteSpace(item.Keys))
            {
                errors.Add($"{file}: 有条目缺少 keys，整份档案跳过");
                return null;
            }

            if (string.IsNullOrWhiteSpace(item.Action))
            {
                errors.Add($"{file}: 条目 \"{item.Keys}\" 缺少 action，整份档案跳过");
                return null;
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
