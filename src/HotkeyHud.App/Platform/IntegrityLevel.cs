using System.Runtime.InteropServices;

namespace HotkeyHud.App.Platform;

/// <summary>
/// 进程完整性级别（UIPI 的判据）。诊断专用——只读，不参与任何判定。
/// <para>
/// 用途：Raw Input 的 <c>RIDEV_INPUTSINK</c> 有一条硬限制——**前台窗口的完整性级别高于本进程时，
/// 收不到任何输入**。若某个应用（例如被以管理员身份启动的 Word）高于 HUD，
/// 长按修饰键就完全不会有日志，表现正是「这个应用里没有面板」。
/// </para>
/// </summary>
internal static class IntegrityLevel
{
    public const int Untrusted = 0x0000;
    public const int Low = 0x1000;
    public const int Medium = 0x2000;
    public const int High = 0x3000;
    public const int System = 0x4000;

    public static int OfCurrentProcess()
    {
        IntPtr token = OpenProcessTokenOf(NativeMethods.GetCurrentProcess());
        return token == IntPtr.Zero ? 0 : ReadRid(token);
    }

    public static int OfProcess(uint processId)
    {
        if (processId == 0)
        {
            return 0;
        }

        IntPtr process = NativeMethods.OpenProcess(
            NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION, false, processId);
        if (process == IntPtr.Zero)
        {
            return 0;
        }

        try
        {
            IntPtr token = OpenProcessTokenOf(process);
            return token == IntPtr.Zero ? 0 : ReadRid(token);
        }
        finally
        {
            NativeMethods.CloseHandle(process);
        }
    }

    /// <summary>前台窗口所属进程的完整性级别；取不到返回 0。</summary>
    public static int OfForeground()
    {
        IntPtr window = NativeMethods.GetForegroundWindow();
        if (window == IntPtr.Zero)
        {
            return 0;
        }

        NativeMethods.GetWindowThreadProcessId(window, out uint processId);
        return OfProcess(processId);
    }

    public static string Name(int rid) => rid switch
    {
        0 => "未知",
        Low => "低",
        Medium => "中",
        High => "高",
        System => "系统",
        _ => $"0x{rid:X}",
    };

    private static IntPtr OpenProcessTokenOf(IntPtr process)
    {
        return NativeMethods.OpenProcessToken(process, NativeMethods.TOKEN_QUERY, out IntPtr token)
            ? token
            : IntPtr.Zero;
    }

    private static int ReadRid(IntPtr token)
    {
        try
        {
            // 先问长度，再一次性取 TOKEN_MANDATORY_LABEL（内含指向 SID 的指针）
            NativeMethods.GetTokenInformation(token, NativeMethods.TokenIntegrityLevel, IntPtr.Zero, 0, out uint size);
            if (size == 0)
            {
                return 0;
            }

            IntPtr buffer = Marshal.AllocHGlobal((int)size);
            try
            {
                if (!NativeMethods.GetTokenInformation(
                        token, NativeMethods.TokenIntegrityLevel, buffer, size, out _))
                {
                    return 0;
                }

                IntPtr sid = Marshal.ReadIntPtr(buffer);
                if (sid == IntPtr.Zero)
                {
                    return 0;
                }

                byte count = Marshal.ReadByte(sid, 1);
                if (count == 0)
                {
                    return 0;
                }

                // GetSidSubAuthority 返回的是「指向该子权限 DWORD 的指针」，必须解引用，不能强转
                IntPtr last = NativeMethods.GetSidSubAuthority(sid, (uint)(count - 1));
                return last == IntPtr.Zero ? 0 : Marshal.ReadInt32(last);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        finally
        {
            NativeMethods.CloseHandle(token);
        }
    }
}
