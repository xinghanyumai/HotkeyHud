namespace HotkeyHud.App.Shell;

/// <summary>
/// 单实例守卫。**只用 <c>Local\</c>**：本工具是每用户每会话的东西——配置在 <c>%APPDATA%</c>、
/// Raw Input 只收本会话输入、托盘图标属于本用户。快速用户切换时两个人各应有一份 HUD，
/// 全局单实例反而是 bug；顺带还省掉了 <c>SeCreateGlobalPrivilege</c> 这条权限失败路径。
/// </summary>
public sealed class SingleInstance : IDisposable
{
    public const string DefaultName = @"Local\HotkeyHud.SingleInstance";

    private readonly string _name;
    private Mutex? _mutex;
    private bool _owned;

    public SingleInstance(string? name = null)
    {
        _name = name ?? DefaultName;
    }

    public bool IsOwned => _owned;

    public bool TryAcquire()
    {
        if (_owned)
        {
            return true;
        }

        try
        {
            var mutex = new Mutex(initiallyOwned: true, _name, out bool createdNew);
            if (createdNew)
            {
                _mutex = mutex;
                _owned = true;
                return true;
            }

            mutex.Dispose();
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            // 连 Local\ 都建不出来：宁可让用户开两个，也不要炸掉启动路径
            return false;
        }
        catch (IOException)
        {
            return false;
        }
    }

    public void Dispose()
    {
        if (_mutex is not null)
        {
            if (_owned)
            {
                try
                {
                    _mutex.ReleaseMutex();
                }
                catch (ApplicationException)
                {
                    // 不是持有者，忽略
                }
            }

            _mutex.Dispose();
            _mutex = null;
            _owned = false;
        }
    }
}
