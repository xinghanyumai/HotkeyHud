using System.Diagnostics;
using System.Text;

namespace HotkeyHud.App;

/// <summary>
/// S7 返工的取证工具：把每次「长按修饰键」的完整判定链写成一行日志，
/// 落在 <c>%TEMP%\hotkeyhud-show.log</c>。
/// <para>
/// 只用 <c>--diag</c> 开启，而该参数仅在 Debug 构建存在；<see cref="Write"/> 又标了
/// <see cref="ConditionalAttribute"/>，Release 编译后调用点整体消失——日志不可能进 Release。
/// </para>
/// <para>
/// 纪律：只观察，不改判定；写失败必须静默，不能连累输入通路（WinExe 无控制台，
/// 所以只落文件，不写 Console）。
/// </para>
/// </summary>
internal static class ShowDiagnostics
{
    private static readonly object Gate = new();
    private static bool _enabled;

    /// <summary>日志是否已开启。调用点用它挡掉昂贵的参数求值（如前台进程查询）。</summary>
    public static bool Enabled => _enabled;

    public static string LogPath { get; } =
        Path.Combine(Path.GetTempPath(), "hotkeyhud-show.log");

    public static void Enable()
    {
        _enabled = true;

        try
        {
            File.WriteAllText(
                LogPath,
                $"# HotkeyHud show log · {DateTime.Now:yyyy-MM-dd HH:mm:ss}{Environment.NewLine}");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[HotkeyHud] 诊断日志初始化失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 写一行。<paramref name="stage"/> 决定这一行在判定链的哪一环：
    /// <c>mod</c> 修饰键上下、<c>nonmod</c> 非修饰键按下、<c>fg</c> 前台切换、
    /// <c>show</c> ShowRequested 已触发、<c>hide</c> 收起。
    /// </summary>
    [Conditional("DEBUG")]
    public static void Write(string stage, params (string Key, string Value)[] fields)
    {
        if (!_enabled)
        {
            return;
        }

        var line = new StringBuilder(224);
        line.Append('[').Append(DateTime.Now.ToString("HH:mm:ss.fff")).Append("] stage=").Append(stage);

        foreach ((string key, string value) in fields)
        {
            line.Append(" | ").Append(key).Append('=').Append(value);
        }

        lock (Gate)
        {
            try
            {
                File.AppendAllText(LogPath, line.AppendLine().ToString(), Encoding.UTF8);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[HotkeyHud] 诊断日志写入失败：{ex.Message}");
            }
        }
    }
}
