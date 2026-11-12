namespace HotkeyHud.Tests;

internal sealed class CheckFailedException : Exception
{
    public CheckFailedException(string message) : base(message)
    {
    }
}

internal static class Check
{
    public static void True(bool condition, string message)
    {
        if (!condition)
        {
            throw new CheckFailedException(message);
        }
    }

    public static void False(bool condition, string message) => True(!condition, message);

    public static void Equal<T>(T expected, T actual, string message)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new CheckFailedException($"{message}（期望 {Describe(expected)}，实际 {Describe(actual)}）");
        }
    }

    public static void NotNull<T>(T? value, string message)
        where T : class
    {
        if (value is null)
        {
            throw new CheckFailedException(message);
        }
    }

    private static string Describe<T>(T value) => value switch
    {
        null => "null",
        string s => $"\"{s}\"",
        _ => value.ToString() ?? "null",
    };
}

/// <summary>用例专用的临时目录，测完即删。</summary>
internal sealed class TempDirectory : IDisposable
{
    public TempDirectory()
    {
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "hotkeyhud-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public void Write(string fileName, string content)
        => File.WriteAllText(System.IO.Path.Combine(Path, fileName), content);

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch
        {
            // 临时目录清理失败不影响用例结论
        }
    }
}
