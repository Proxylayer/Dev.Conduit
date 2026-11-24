using System.Runtime.Versioning;
using System.Security.Principal;

namespace Proxylayer.DevConduit.Tray;

/// <summary>
/// Keeps the tray to one instance per user in a session. The first instance holds a named
/// mutex; a later launch finds it held, signals a named event the first instance waits on,
/// and exits. The first instance answers the signal by opening its setup window.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class SingleInstance : IDisposable
{
    private readonly Mutex _mutex;
    private readonly EventWaitHandle _activation;
    private RegisteredWaitHandle? _registration;

    private SingleInstance(Mutex mutex, EventWaitHandle activation)
    {
        _mutex = mutex;
        _activation = activation;
    }

    /// <summary>The mutex, or null when another instance already holds it.</summary>
    public static SingleInstance? TryAcquire()
    {
        var mutex = new Mutex(initiallyOwned: true, MutexName(), out var createdNew);
        if (!createdNew)
        {
            mutex.Dispose();
            return null;
        }

        var activation = new EventWaitHandle(initialState: false, EventResetMode.AutoReset, EventName());
        return new SingleInstance(mutex, activation);
    }

    /// <summary>Asks the running instance to show itself. False when it could not be reached.</summary>
    public static bool SignalRunningInstance()
    {
        if (!EventWaitHandle.TryOpenExisting(EventName(), out var activation))
        {
            return false;
        }

        using (activation)
        {
            return activation.Set();
        }
    }

    /// <summary>Calls <paramref name="callback"/>, on a thread-pool thread, each time a later launch signals.</summary>
    public void OnActivated(Action callback)
    {
        _registration?.Unregister(null);
        _registration = ThreadPool.RegisterWaitForSingleObject(
            _activation,
            (_, _) => callback(),
            state: null,
            Timeout.Infinite,
            executeOnlyOnce: false);
    }

    public void Dispose()
    {
        _registration?.Unregister(null);
        _activation.Dispose();
        _mutex.ReleaseMutex();
        _mutex.Dispose();
    }

    private static string MutexName() => $@"Local\Dev.Conduit.Tray.{UserKey()}";

    private static string EventName() => $@"Local\Dev.Conduit.Tray.Activate.{UserKey()}";

    private static string UserKey()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return identity.User?.Value ?? Environment.UserName;
    }
}
