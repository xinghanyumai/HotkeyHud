using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace HotkeyHud.App.Platform;

public enum InputKind
{
    Key,
    MouseButton,
    MouseWheel,
}

/// <summary>采集到的一次输入。时间戳为单调时钟毫秒，与系统时间无关。</summary>
public readonly record struct InputEvent(InputKind Kind, int Code, bool IsDown, long TimestampMs);

/// <summary>
/// Raw Input 采集器。
/// 回调内只做「解析 + 入队 + 投递消息」，不调用任何 UI、不做 I/O（坑 #12）。
/// </summary>
public sealed class RawInputSource : IDisposable
{
    private const uint RIDEV_REMOVE = 0x00000001;
    private const int WM_DRAIN = NativeMethods.WM_APP + 1;
    private const int BufferSize = 128;

    private readonly ConcurrentQueue<InputEvent> _queue = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly RawInputWindow _window;
    private readonly IntPtr _buffer;
    private bool _disposed;

    public RawInputSource()
    {
        _buffer = Marshal.AllocHGlobal(BufferSize);
        _window = new RawInputWindow(this);
        _window.CreateHandle(new CreateParams
        {
            Caption = "HotkeyHudRawInput",
            X = 0,
            Y = 0,
            Width = 0,
            Height = 0,
            Style = NativeMethods.WS_POPUP,
            ExStyle = 0,
            Param = IntPtr.Zero,
        });

        var devices = new[]
        {
            new NativeMethods.RAWINPUTDEVICE
            {
                usUsagePage = 0x01,
                usUsage = 0x06, // keyboard
                dwFlags = NativeMethods.RIDEV_INPUTSINK,
                hwndTarget = _window.Handle,
            },
            new NativeMethods.RAWINPUTDEVICE
            {
                usUsagePage = 0x01,
                usUsage = 0x02, // mouse
                dwFlags = NativeMethods.RIDEV_INPUTSINK,
                hwndTarget = _window.Handle,
            },
        };

        if (!NativeMethods.RegisterRawInputDevices(
                devices, (uint)devices.Length, (uint)Marshal.SizeOf<NativeMethods.RAWINPUTDEVICE>()))
        {
            throw new InvalidOperationException(
                $"RegisterRawInputDevices 失败，Win32 错误码 {Marshal.GetLastWin32Error()}");
        }
    }

    /// <summary>在 UI 线程上触发；事件参数携带采集时刻的单调时间戳。</summary>
    public event Action<InputEvent>? Input;

    /// <summary>已解析的输入事件总数（用于自检）。</summary>
    public long ReceivedCount => Interlocked.Read(ref _receivedCount);

    /// <summary>单次 WM_INPUT 回调内的最大耗时，毫秒（用于自检「不阻塞输入」）。</summary>
    public double MaxCallbackMs { get; private set; }

    /// <summary>是否采样回调耗时。默认关闭，避免诊断代码进入生产热路径。</summary>
    public bool CollectStats { get; set; }

    /// <summary>回调耗时的 99 分位，毫秒。用分位数而非最大值，避免把 GC 偶发停顿当成设计缺陷。</summary>
    public double P99CallbackMs
    {
        get
        {
            if (_samples.Count == 0)
            {
                return 0;
            }

            var sorted = _samples.OrderBy(v => v).ToList();
            return sorted[(int)((sorted.Count - 1) * 0.99)];
        }
    }

    /// <summary>清零耗时统计。首次回调含 JIT 与封送初始化，测量前需预热并重置。</summary>
    public void ResetCallbackStats()
    {
        MaxCallbackMs = 0;
        _samples.Clear();
    }

    private readonly List<double> _samples = new();

    private long _receivedCount;

    public IntPtr Handle => _window.Handle;

    internal void OnRawInput(IntPtr lParam)
    {
        long start = Stopwatch.GetTimestamp();
        try
        {
            uint headerSize = (uint)Marshal.SizeOf<NativeMethods.RAWINPUTHEADER>();
            uint size = BufferSize;
            uint got = NativeMethods.GetRawInputData(
                lParam, NativeMethods.RID_INPUT, _buffer, ref size, headerSize);
            if (got == 0 || got == unchecked((uint)-1))
            {
                return;
            }

            var raw = Marshal.PtrToStructure<NativeMethods.RAWINPUT>(_buffer);
            long ts = ElapsedMs();

            if (raw.header.dwType == NativeMethods.RIM_TYPEKEYBOARD)
            {
                var kb = raw.data.keyboard;
                // 0xFF = VK__none_，由 Alt+Tab / 假键产生
                if (kb.VKey == 0 || kb.VKey == 0xFF)
                {
                    return;
                }

                bool down = kb.Message is NativeMethods.WM_KEYDOWN or NativeMethods.WM_SYSKEYDOWN;
                bool up = kb.Message is NativeMethods.WM_KEYUP or NativeMethods.WM_SYSKEYUP;
                if (!down && !up)
                {
                    return;
                }

                Enqueue(new InputEvent(InputKind.Key, kb.VKey, down, ts));
            }
            else if (raw.header.dwType == NativeMethods.RIM_TYPEMOUSE)
            {
                var mouse = raw.data.mouse;
                ushort flags = mouse.usButtonFlags;
                if (flags == 0)
                {
                    return; // 纯移动，S1 不关心
                }

                AppendMouseButton(flags, NativeMethods.RI_MOUSE_LEFT_BUTTON_DOWN, NativeMethods.RI_MOUSE_LEFT_BUTTON_UP, 1, ts);
                AppendMouseButton(flags, NativeMethods.RI_MOUSE_RIGHT_BUTTON_DOWN, NativeMethods.RI_MOUSE_RIGHT_BUTTON_UP, 2, ts);
                AppendMouseButton(flags, NativeMethods.RI_MOUSE_MIDDLE_BUTTON_DOWN, NativeMethods.RI_MOUSE_MIDDLE_BUTTON_UP, 3, ts);
                AppendMouseButton(flags, NativeMethods.RI_MOUSE_BUTTON_4_DOWN, NativeMethods.RI_MOUSE_BUTTON_4_UP, 4, ts);
                AppendMouseButton(flags, NativeMethods.RI_MOUSE_BUTTON_5_DOWN, NativeMethods.RI_MOUSE_BUTTON_5_UP, 5, ts);

                if ((flags & (NativeMethods.RI_MOUSE_WHEEL | NativeMethods.RI_MOUSE_HWHEEL)) != 0)
                {
                    short delta = unchecked((short)mouse.usButtonData);
                    Enqueue(new InputEvent(InputKind.MouseWheel, delta, false, ts));
                }
            }
        }
        finally
        {
            double ms = (Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency;
            if (ms > MaxCallbackMs)
            {
                MaxCallbackMs = ms;
            }

            if (CollectStats && _samples.Count < 50000)
            {
                _samples.Add(ms);
            }
        }
    }

    private void AppendMouseButton(ushort flags, ushort downBit, ushort upBit, int button, long ts)
    {
        if ((flags & downBit) != 0)
        {
            Enqueue(new InputEvent(InputKind.MouseButton, button, true, ts));
        }

        if ((flags & upBit) != 0)
        {
            Enqueue(new InputEvent(InputKind.MouseButton, button, false, ts));
        }
    }

    private void Enqueue(InputEvent evt)
    {
        _queue.Enqueue(evt);
        // 唤醒 UI 线程去队列化，回调本身立刻返回
        NativeMethods.PostMessage(_window.Handle, WM_DRAIN, IntPtr.Zero, IntPtr.Zero);
    }

    internal void Drain()
    {
        while (_queue.TryDequeue(out var evt))
        {
            Interlocked.Increment(ref _receivedCount);
            Input?.Invoke(evt);
        }
    }

    private long ElapsedMs() => (long)(_clock.ElapsedTicks * 1000.0 / Stopwatch.Frequency);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        var devices = new[]
        {
            new NativeMethods.RAWINPUTDEVICE { usUsagePage = 0x01, usUsage = 0x06, dwFlags = RIDEV_REMOVE, hwndTarget = IntPtr.Zero },
            new NativeMethods.RAWINPUTDEVICE { usUsagePage = 0x01, usUsage = 0x02, dwFlags = RIDEV_REMOVE, hwndTarget = IntPtr.Zero },
        };
        NativeMethods.RegisterRawInputDevices(
            devices, (uint)devices.Length, (uint)Marshal.SizeOf<NativeMethods.RAWINPUTDEVICE>());

        _window.DestroyHandle();
        Marshal.FreeHGlobal(_buffer);
    }

    /// <summary>
    /// 接收 WM_INPUT 的真实顶层窗口（坑 #3：不能用 HWND_MESSAGE，否则收不到）。
    /// 尺寸 0×0、从不显示，用户不可见。
    /// </summary>
    private sealed class RawInputWindow : NativeWindow
    {
        private readonly RawInputSource _owner;

        public RawInputWindow(RawInputSource owner) => _owner = owner;

        protected override void WndProc(ref Message m)
        {
            // 输入/系统回调必须吞异常（红线 3），否则整条链路静默失效
            try
            {
                switch (m.Msg)
                {
                    case NativeMethods.WM_INPUT:
                        _owner.OnRawInput(m.LParam);
                        return;
                    case WM_DRAIN:
                        _owner.Drain();
                        return;
                }
            }
            catch (Exception ex)
            {
                // 吞掉：Raw Input 回调中抛异常会让后续输入全部丢失（红线 3）。
                // 但调试构建必须留下痕迹，否则问题会彻底静默。
                Debug.WriteLine($"[HotkeyHud] 输入回调异常：{ex}");
                return;
            }

            base.WndProc(ref m);
        }
    }
}
