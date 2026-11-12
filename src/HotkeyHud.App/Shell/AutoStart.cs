using Microsoft.Win32;

namespace HotkeyHud.App.Shell;

/// <summary>
/// 开机自启：写 <c>HKCU\Software\Microsoft\Windows\CurrentVersion\Run</c>。
/// <para>
/// 路径必须带引号（否则 <c>C:\Program Files\...</c> 会被拆成两个参数），
/// 且取 <see cref="Environment.ProcessPath"/>——<c>Assembly.Location</c> 在单文件发布下是空串。
/// </para>
/// </summary>
public static class AutoStart
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "HotkeyHud";

    /// <summary>注册表里应当写入的命令行。</summary>
    public static string ExpectedCommand()
    {
        string? path = Environment.ProcessPath;
        if (string.IsNullOrEmpty(path))
        {
            throw new InvalidOperationException("Environment.ProcessPath 为空，无法确定自身路径");
        }

        return $"\"{path}\"";
    }

    public static bool IsEnabled()
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
        return key?.GetValue(ValueName) is string value && value.Length > 0;
    }

    public static string? CurrentCommand()
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
        return key?.GetValue(ValueName) as string;
    }

    public static void Set(bool enabled)
    {
        using RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true)
                                ?? throw new InvalidOperationException($"无法打开注册表键 {RunKeyPath}");

        if (enabled)
        {
            key.SetValue(ValueName, ExpectedCommand(), RegistryValueKind.String);
        }
        else
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }
}
