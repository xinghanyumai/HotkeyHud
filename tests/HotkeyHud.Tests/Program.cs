using System.Text;

namespace HotkeyHud.Tests;

internal static class Program
{
    private static int _failed;
    private static int _passed;

    private static int Main()
    {
        try
        {
            Console.OutputEncoding = new UTF8Encoding(false);
        }
        catch
        {
            // 无控制台时忽略
        }

        Cases.RunAll();

        Console.WriteLine($"--- 通过 {_passed}，失败 {_failed} ---");
        return _failed;
    }

    internal static void Run(string name, Action body)
    {
        try
        {
            body();
            _passed++;
            Console.WriteLine($"[PASS] {name}");
        }
        catch (CheckFailedException ex)
        {
            _failed++;
            Console.WriteLine($"[FAIL] {name} — {ex.Message}");
        }
        catch (Exception ex)
        {
            _failed++;
            Console.WriteLine($"[FAIL] {name} — {ex.GetType().Name}: {ex.Message}");
        }
    }
}
