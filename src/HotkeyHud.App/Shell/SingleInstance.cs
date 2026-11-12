namespace HotkeyHud.App.Shell;

/// <summary>
/// 单实例守卫。<c>Global\</c> 命名对象在部分权限配置下需要 <c>SeCreateGlobalPrivilege</c>，
/// 建不出来就退到 <c>Local\</c>——同一台机器上多用户各跑一份也能接受，总比崩掉好。
/// </summary>
public sealed class SingleInstance : IDisposable
{
    public const string GlobalName = @"Global\HotkeyHud.SingleInstance";
    private const string LocalName = @"Local\HotkeyHud.SingleInstance";

    private readonly string _name;
    private Mutex? _mutex;
    private bool _owned;

    public SingleInstance(string? name = null)
    {
        _name = name ?? GlobalName;
    }

    public bool IsOwned => _owned;

    public bool TryAcquire()
    {
        if (_owned)
        {
            return true;
        }

        foreach (string candidate in Candidates())
        {
            try
            {
                var mutex = new Mutex(initiallyOwned: true, candidate, out bool createdNew);
                if (createdNew)
                {
                    _mutex = mutex;
                    _owned = true;
                    return true;
                }

                mutex.Dispose();
                return false; // 名字能建但已被占用：确实是第二个实例
            }
            catch (UnauthorizedAccessException)
            {
                // 建不出 Global\ 就试 Local\，不要把启动路径炸掉
            }
            catch (IOException)
            {
                return false;
            }
        }

        return false;
    }

    private IEnumerable<string> Candidates()
    {
        yield return _name;

        if (string.Equals(_name, GlobalName, StringComparison.Ordinal))
        {
            yield return LocalName;
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
