namespace ClipCompose.Services;

/// <summary>
/// 同一ユーザーの多重起動の防止（仕様書 §3）。2 つ目の起動は既存プロセスへ通知して終了し、既存側が選択画面を表示する
/// （起動を拒否するだけにして画面を見失わせない）。
/// </summary>
public sealed class SingleInstance : IDisposable
{
    private const string MutexName = @"Local\ClipCompose.SingleInstance";
    private const string EventName = @"Local\ClipCompose.Activate";

    private readonly Mutex _mutex;
    private readonly EventWaitHandle _activateEvent;
    private RegisteredWaitHandle? _registration;

    private SingleInstance(Mutex mutex, EventWaitHandle activateEvent)
    {
        _mutex = mutex;
        _activateEvent = activateEvent;
    }

    /// <summary>
    /// 最初のインスタンスなら取得して返す。既に起動済みなら既存側へ通知して null。
    /// <paramref name="waitForPrevious"/> が正なら、既存側が終了して解放するまでその時間だけ待つ（言語変更の再起動用）。
    /// </summary>
    public static SingleInstance? TryAcquire(TimeSpan waitForPrevious = default)
    {
        var mutex = new Mutex(initiallyOwned: true, MutexName, out var createdNew);
        var activateEvent = new EventWaitHandle(false, EventResetMode.AutoReset, EventName);
        if (createdNew || (waitForPrevious > TimeSpan.Zero && WaitForRelease(mutex, waitForPrevious)))
            return new SingleInstance(mutex, activateEvent);

        // 前面に出す権利は利用者が起動したこちら側にある。既存側が選択画面を前面に出せるよう譲る。
        Interop.NativeMethods.AllowSetForegroundWindow(Interop.NativeMethods.ASFW_ANY);
        activateEvent.Set();
        activateEvent.Dispose();
        mutex.Dispose();
        return null;
    }

    private static bool WaitForRelease(Mutex mutex, TimeSpan timeout)
    {
        try
        {
            return mutex.WaitOne(timeout);
        }
        catch (AbandonedMutexException)
        {
            return true; // 前のプロセスが解放せずに終わった。所有権はこちらに移っている。
        }
    }

    /// <summary>2 つ目の起動からの通知を受けたときの処理（スレッドプールから呼ばれる）。</summary>
    public void ListenForActivation(Action onActivate)
    {
        _registration = ThreadPool.RegisterWaitForSingleObject(_activateEvent, (_, _) => onActivate(), null, Timeout.Infinite, executeOnlyOnce: false);
    }

    public void Dispose()
    {
        _registration?.Unregister(null);
        _activateEvent.Dispose();
        try { _mutex.ReleaseMutex(); } catch (ApplicationException) { }
        _mutex.Dispose();
    }
}
