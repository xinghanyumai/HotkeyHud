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
    private readonly string? _userDirectory;
    private Dictionary<string, AppShortcutProfile> _byProcess = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<string> _loadErrors = Array.Empty<string>();
    private int _profileCount;
    private int _userProfileCount;
    private IReadOnlyList<AppShortcutProfile> _profiles = Array.Empty<AppShortcutProfile>();

    /// <summary>系统档案（<c>processNames: ["*"]</c>）。未收录应用时仍然显示它。</summary>
    public AppShortcutProfile? SystemProfile { get; private set; }

    /// <summary>用户全局档案（<c>%APPDATA%\HotkeyHud\apps\_global.json</c>），组名「自定义」。</summary>
    public AppShortcutProfile? GlobalProfile { get; private set; }

    /// <param name="appsDirectory">内置档案目录（随 exe 输出，只读）。</param>
    /// <param name="userAppsDirectory">用户档案目录（可写）。为 null 时只加载内置档案。</param>
    public ShortcutRepository(string appsDirectory, string? userAppsDirectory = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appsDirectory);
        _directory = appsDirectory;
        _userDirectory = userAppsDirectory;
        Reload();
    }

    public int ProfileCount => _profileCount;

    /// <summary>用户目录里被合并的档案数（一个 exe 一份）。</summary>
    public int UserProfileCount => _userProfileCount;

    /// <summary>全部应用档案（内置在前、用户在后），设置页用它列应用。</summary>
    public IReadOnlyList<AppShortcutProfile> Profiles => _profiles;

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
        AppShortcutProfile? systemProfile = null;

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

                // 通配档案（系统快捷键）单独收，不参与进程名索引，也不计入 ProfileCount
                if (profile.ProcessNames.Any(name => name.Trim() == "*"))
                {
                    if (systemProfile is null)
                    {
                        systemProfile = profile;
                    }
                    else
                    {
                        errors.Add($"{file}: 已存在系统档案，整份跳过");
                    }

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

        // §3.4.1：用户目录后加载——内置先、用户后；同 keys 用户覆盖内置
        AppShortcutProfile? global = null;
        int userProfiles = 0;
        if (!string.IsNullOrWhiteSpace(_userDirectory) && Directory.Exists(_userDirectory))
        {
            LoadUserProfiles(byProcess, errors, ref dropped, ref userProfiles, ref global);
        }

        var ordered = new List<AppShortcutProfile>();
        var added = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (AppShortcutProfile profile in byProcess.Values.OrderBy(p => p.App, StringComparer.OrdinalIgnoreCase))
        {
            if (added.Add(profile.App))
            {
                ordered.Add(profile);
            }
        }

        _profiles = ordered;
        _byProcess = byProcess;
        _loadErrors = errors;
        _profileCount = loaded;
        _userProfileCount = userProfiles;
        SystemProfile = systemProfile;
        GlobalProfile = global;
        DroppedShortcutCount = dropped;
    }

    /// <summary>
    /// 加载用户档案并与内置档案合并。文件名即进程名（`notepad.json` → notepad），
    /// 也接受档案里显式写 <c>processNames</c>——但托盘入口会自动填，不要求用户手写。
    /// </summary>
    private void LoadUserProfiles(
        Dictionary<string, AppShortcutProfile> byProcess,
        List<string> errors,
        ref int dropped,
        ref int userProfiles,
        ref AppShortcutProfile? global)
    {
        foreach (string file in Directory
                     .EnumerateFiles(_userDirectory!, "*.json")
                     .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            bool isGlobal = string.Equals(
                Path.GetFileNameWithoutExtension(file), "_global", StringComparison.OrdinalIgnoreCase);

            AppShortcutProfile? user = TryLoadProfile(file, ref dropped, errors, markAsUser: true);
            if (user is null)
            {
                continue;
            }

            if (isGlobal)
            {
                if (global is null)
                {
                    global = WithDefaultGroup(user, "自定义");
                }
                else
                {
                    errors.Add($"{file}: 已存在 _global.json，整份跳过");
                }

                continue;
            }

            // 用户档案没写 processNames 时用文件名兜底（托盘生成的文件一定带，手写的可能漏）
            string[] names = user.ProcessNames.Length > 0
                ? user.ProcessNames
                : new[] { Path.GetFileNameWithoutExtension(file) };

            var keys = new List<string>();
            foreach (string name in names)
            {
                string key = Normalize(name);
                if (key.Length != 0)
                {
                    keys.Add(key);
                }
            }

            if (keys.Count == 0)
            {
                errors.Add($"{file}: 推不出进程名（文件名与 processNames 都为空）");
                continue;
            }

            foreach (string key in keys)
            {
                AppShortcutProfile merged = byProcess.TryGetValue(key, out AppShortcutProfile? builtin)
                    ? Merge(builtin, user)
                    : user;

                byProcess[key] = new AppShortcutProfile
                {
                    App = merged.App,
                    DisplayName = merged.DisplayName,
                    ProcessNames = merged.ProcessNames,
                    IsUserOwned = true,
                    Shortcuts = merged.Shortcuts,
                };
            }

            userProfiles++;
        }
    }

    /// <summary>同 keys 用户覆盖内置；用户独有的条目追加在末尾（§3.4.1）。</summary>
    private static AppShortcutProfile Merge(AppShortcutProfile builtin, AppShortcutProfile user)
    {
        var byKey = new Dictionary<string, ShortcutEntry>(StringComparer.OrdinalIgnoreCase);
        var order = new List<string>();

        foreach (ShortcutEntry entry in builtin.Shortcuts)
        {
            string key = DedupeKey(entry);
            if (byKey.TryAdd(key, entry))
            {
                order.Add(key);
            }
        }

        foreach (ShortcutEntry entry in user.Shortcuts)
        {
            string key = DedupeKey(entry);
            if (byKey.ContainsKey(key))
            {
                byKey[key] = entry; // 覆盖，但保留内置里的位置，组顺序不被打乱
            }
            else
            {
                byKey[key] = entry;
                order.Add(key);
            }
        }

        var shortcuts = new List<ShortcutEntry>(order.Count);
        foreach (string key in order)
        {
            shortcuts.Add(byKey[key]);
        }

        return new AppShortcutProfile
        {
            App = builtin.App,
            DisplayName = string.IsNullOrWhiteSpace(user.DisplayName) ? builtin.DisplayName : user.DisplayName,
            ProcessNames = builtin.ProcessNames,
            Shortcuts = shortcuts,
        };
    }

    private static AppShortcutProfile WithDefaultGroup(AppShortcutProfile profile, string group)
    {
        var shortcuts = new List<ShortcutEntry>(profile.Shortcuts.Count);
        foreach (ShortcutEntry entry in profile.Shortcuts)
        {
            shortcuts.Add(string.IsNullOrWhiteSpace(entry.Group)
                ? new ShortcutEntry
                {
                    Source = entry.Source,
                    Keys = entry.Keys,
                    Action = entry.Action,
                    Group = group,
                    Combo = entry.Combo,
                    IsValid = entry.IsValid,
                }
                : entry);
        }

        return new AppShortcutProfile
        {
            App = profile.App,
            DisplayName = profile.DisplayName,
            ProcessNames = profile.ProcessNames,
            Shortcuts = shortcuts,
        };
    }

    /// <summary>去重键：能解析就用规范化组合（`Ctrl+F` 与 `control+f` 视为同一条），否则用原文。</summary>
    private static string DedupeKey(ShortcutEntry entry)
        => entry.IsValid ? Keys.Format(entry.Combo) : entry.Keys.Trim().ToLowerInvariant();

    private static AppShortcutProfile? TryLoadProfile(
        string file, ref int dropped, List<string> errors, bool markAsUser = false)
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

        // 用户档案允许省略 processNames：文件名就是进程名（托盘入口会自动填，手写的可能漏）
        if (!markAsUser && (document.ProcessNames is null || document.ProcessNames.Length == 0))
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

            string? color = HudColors.Normalize(item.Color);
            if (!string.IsNullOrWhiteSpace(item.Color) && color is null)
            {
                // 只接受 8 个预设值：其他值按缺省处理并留证
                errors.Add($"{file}: 条目 \"{item.Keys}\" 的颜色 \"{item.Color}\" 不是预设值，已按默认色处理");
            }

            shortcuts.Add(new ShortcutEntry
            {
                Source = markAsUser ? ShortcutSource.User : ShortcutSource.Builtin,
                Keys = item.Keys,
                Action = item.Action,
                Group = item.Group,
                Color = color,
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
            ProcessNames = document.ProcessNames ?? Array.Empty<string>(),
            Shortcuts = shortcuts,
        };
    }

    /// <summary>
    /// 把整份档案写成用户副本（§3.4.1）。**只写用户目录，绝不碰安装目录的内置档案。**
    /// 返回写入路径；没配用户目录时抛 <see cref="InvalidOperationException"/>。
    /// </summary>
    public string SaveUserProfile(AppShortcutProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (string.IsNullOrWhiteSpace(_userDirectory))
        {
            throw new InvalidOperationException("没有配置用户档案目录，无法保存");
        }

        Directory.CreateDirectory(_userDirectory);

        var document = new Dictionary<string, object?>
        {
            ["app"] = profile.App,
            ["displayName"] = profile.DisplayName,
            ["processNames"] = profile.ProcessNames,
            ["shortcuts"] = profile.Shortcuts.Select(entry => new Dictionary<string, object?>
            {
                ["keys"] = entry.Keys,
                ["action"] = entry.Action,
                ["group"] = entry.Group,
                ["color"] = entry.Color,
            }).ToList(),
        };

        string path = Path.Combine(_userDirectory, profile.App + ".json");
        File.WriteAllText(
            path,
            JsonSerializer.Serialize(document, new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            }));

        Reload();
        return path;
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

        [JsonPropertyName("color")]
        public string? Color { get; set; }

        [JsonPropertyName("action")]
        public string? Action { get; set; }

        [JsonPropertyName("group")]
        public string? Group { get; set; }
    }
}
