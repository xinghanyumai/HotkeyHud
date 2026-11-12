using System.Text.Json;
using KeyNames = HotkeyHud.Core.Keys;
using System.Text.Json.Serialization;
using HotkeyHud.Core;

namespace HotkeyHud.App.Shell;

/// <summary>
/// <c>%APPDATA%\HotkeyHud\config.json</c> 的读写。
/// <para>
/// 损坏（JSON 语法错、字段类型错、取值越界、熟记名单里有解析不了的组合）→
/// 用默认值启动，把原文件改名成 <c>config.broken.json</c> 留证，并让调用方弹一次托盘气泡。
/// **不静默吞掉**：用户必须知道自己的配置没生效。
/// </para>
/// </summary>
public sealed class ConfigStore
{
    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public ConfigStore(string? directory = null)
    {
        DirectoryPath = directory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "HotkeyHud");
        FilePath = Path.Combine(DirectoryPath, "config.json");
        BrokenPath = Path.Combine(DirectoryPath, "config.broken.json");
    }

    public string DirectoryPath { get; }

    public string FilePath { get; }

    public string BrokenPath { get; }

    /// <summary>最近一次 <see cref="Load"/> 是否因为配置损坏而回落到默认值。</summary>
    public bool LastLoadWasCorrupt { get; private set; }

    /// <summary>最近一次损坏的原因，正常时为空串。</summary>
    public string LastCorruptReason { get; private set; } = string.Empty;

    /// <summary>
    /// 最近一次 <see cref="Load"/> 做过的「钳制」说明（值越界但整份配置仍然可用）。
    /// 与损坏分开：钳制保留其它字段，损坏才整份作废。
    /// </summary>
    public IReadOnlyList<string> LastRepairs { get; private set; } = Array.Empty<string>();

    public AppConfig Load()
    {
        LastLoadWasCorrupt = false;
        LastCorruptReason = string.Empty;
        LastRepairs = Array.Empty<string>();

        if (!File.Exists(FilePath))
        {
            AppConfig fresh = new();
            TrySave(fresh);
            return fresh;
        }

        try
        {
            string json = File.ReadAllText(FilePath);
            ConfigDocument? document = JsonSerializer.Deserialize<ConfigDocument>(json, ReadOptions);
            if (document is null)
            {
                throw new ConfigCorruptException("配置内容为空");
            }

            return Validate(document);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException
                                       or ConfigCorruptException or ArgumentException)
        {
            LastLoadWasCorrupt = true;
            LastCorruptReason = ex.Message;
            PreserveBrokenFile();

            AppConfig fallback = new();
            TrySave(fallback);
            return fallback;
        }
    }

    public void Save(AppConfig config)
    {
        System.IO.Directory.CreateDirectory(DirectoryPath);

        var document = new ConfigDocument
        {
            DwellMs = config.DwellMs,
            KnownShortcuts = config.KnownShortcuts?.ToList(),
            ExtraGameProcesses = config.ExtraGameProcesses.ToList(),
            StartWithWindows = config.StartWithWindows,
        };

        File.WriteAllText(FilePath, JsonSerializer.Serialize(document, WriteOptions));
    }

    private AppConfig Validate(ConfigDocument document)
    {
        int dwellMs = document.DwellMs ?? AppConfig.DefaultDwellMs;

        // 值越界 → 钳制到边界并保留其它字段。整份作废的代价太大：
        // 用户只想调 dwellMs，手滑写成 60000，不该连 knownShortcuts 一起丢。
        if (dwellMs < AppConfig.MinDwellMs || dwellMs > AppConfig.MaxDwellMs)
        {
            int clamped = Math.Clamp(dwellMs, AppConfig.MinDwellMs, AppConfig.MaxDwellMs);
            LastRepairs = new[] { $"dwellMs = {dwellMs} 超出范围，已改为 {clamped}" };
            dwellMs = clamped;
        }

        // 熟记名单里出现解析不了的组合 → 按损坏处理。这不是「值越界」，
        // 是内容本身没意义；而且 KnownShortcutFilter 会在启动时直接抛。
        if (document.KnownShortcuts is { } combos)
        {
            foreach (string combo in combos)
            {
                if (!KeyNames.TryParse(combo, out _))
                {
                    throw new ConfigCorruptException($"knownShortcuts 里有无法解析的组合 \"{combo}\"");
                }
            }
        }

        return new AppConfig
        {
            DwellMs = dwellMs,
            KnownShortcuts = document.KnownShortcuts,
            ExtraGameProcesses = document.ExtraGameProcesses ?? new List<string>(),
            StartWithWindows = document.StartWithWindows ?? false,
        };
    }

    private void PreserveBrokenFile()
    {
        try
        {
            System.IO.Directory.CreateDirectory(DirectoryPath);
            File.Move(FilePath, BrokenPath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 留证失败不能连累启动
        }
    }

    private void TrySave(AppConfig config)
    {
        try
        {
            Save(config);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 无写权限时照常运行，只是下次启动还得用默认值
        }
    }

    private sealed class ConfigDocument
    {
        [JsonPropertyName("dwellMs")]
        public int? DwellMs { get; set; }

        [JsonPropertyName("knownShortcuts")]
        public List<string>? KnownShortcuts { get; set; }

        [JsonPropertyName("extraGameProcesses")]
        public List<string>? ExtraGameProcesses { get; set; }

        [JsonPropertyName("startWithWindows")]
        public bool? StartWithWindows { get; set; }
    }

    private sealed class ConfigCorruptException : Exception
    {
        public ConfigCorruptException(string message) : base(message)
        {
        }
    }
}
