using System.Text;
using System.Text.Json;

namespace HotkeyHud.App.Shell;

/// <summary>适配器读到的一条快捷键（键位已转成本产品的写法）。</summary>
internal readonly record struct AdapterEntry(string Keys, string Action);

/// <summary>
/// 用户档案的写入与模板（§3.4.1）。目录固定 <c>%APPDATA%\HotkeyHud\apps</c>，
/// 文件名即进程名——进程名由选中的 exe 自动取，不让用户手写。
/// </summary>
internal static class UserApps
{
    /// <summary>生成给用户改的模板。带 <c>//</c> 注释，加载器已允许注释与尾逗号。</summary>
    public static string Template(string exePath)
    {
        string fileName = Path.GetFileName(exePath);
        string app = Path.GetFileNameWithoutExtension(exePath);
        string display = app;

        var builder = new StringBuilder();
        builder.AppendLine("// HotkeyHud 用户档案 —— 保存后到托盘菜单点「重载数据」即可生效");
        builder.AppendLine("// keys 写法：Ctrl / Alt / Shift / Win + 键名，例如 Ctrl+Alt+F、Ctrl+Shift+空格 写成 Ctrl+Shift+Space");
        builder.AppendLine("// group 是面板里的分组名；不写就归入「自定义」");
        builder.AppendLine("{");
        builder.AppendLine($"  \"app\": {JsonSerializer.Serialize(app)},");
        builder.AppendLine($"  \"displayName\": {JsonSerializer.Serialize(display)},");
        builder.AppendLine($"  \"processNames\": [{JsonSerializer.Serialize(fileName)}],");
        builder.AppendLine("  \"shortcuts\": [");
        builder.AppendLine("    // 下面三条只是示例，改成你自己的；不需要就整行删掉");
        builder.AppendLine("    { \"keys\": \"Ctrl+Alt+1\", \"action\": \"示例动作一\", \"group\": \"自定义\" },");
        builder.AppendLine("    { \"keys\": \"Ctrl+Alt+2\", \"action\": \"示例动作二\", \"group\": \"自定义\" },");
        builder.AppendLine("    { \"keys\": \"Ctrl+Alt+3\", \"action\": \"示例动作三\", \"group\": \"自定义\" }");
        builder.AppendLine("  ]");
        builder.AppendLine("}");
        return builder.ToString();
    }

    /// <summary>把模板写到用户目录，返回文件路径。</summary>
    public static string WriteTemplate(string directory, string exePath)
    {
        string path = PathFor(directory, exePath);
        Directory.CreateDirectory(directory);
        File.WriteAllText(path, Template(exePath));
        return path;
    }

    /// <summary>把适配器读到的条目写成用户档案。</summary>
    public static string WriteEntries(
        string directory, string exePath, IEnumerable<AdapterEntry> entries, string group)
    {
        string fileName = Path.GetFileName(exePath);
        string app = Path.GetFileNameWithoutExtension(exePath);

        var shortcuts = entries.Select(entry => new Dictionary<string, string>
        {
            ["keys"] = entry.Keys,
            ["action"] = entry.Action,
            ["group"] = group,
        }).ToList();

        var document = new Dictionary<string, object>
        {
            ["app"] = app,
            ["displayName"] = app,
            ["processNames"] = new[] { fileName },
            ["shortcuts"] = shortcuts,
        };

        string path = PathFor(directory, exePath);
        Directory.CreateDirectory(directory);
        File.WriteAllText(
            path,
            JsonSerializer.Serialize(document, new JsonSerializerOptions { WriteIndented = true }));
        return path;
    }

    /// <summary>用户档案路径：<c>&lt;dir&gt;\notepad.json</c>（去掉 .exe，与内置档案同名规则一致）。</summary>
    public static string PathFor(string directory, string exePath)
        => Path.Combine(directory, Path.GetFileNameWithoutExtension(exePath) + ".json");
}
