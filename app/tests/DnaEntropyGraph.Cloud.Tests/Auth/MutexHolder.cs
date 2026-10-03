using DnaEntropyGraph.Cloud.Auth;

namespace DnaEntropyGraph.Cloud.Tests.Auth;

/// <summary>Holds the accounts-file mutex for a folder on a thread of its own (a mutex belongs to the thread that took it) until disposed: another copy of the app mid-save.</summary>
internal sealed class MutexHolder : IDisposable
{
    private readonly ManualResetEventSlim _release = new();
    private readonly Thread _thread;

    public MutexHolder(string authDirectory)
    {
        using var ready = new ManualResetEventSlim();
        _thread = new Thread(() =>
        {
            using var mutex = new Mutex(false, AccountRegistry.MutexNameFor(authDirectory));
            mutex.WaitOne();
            ready.Set();
            _release.Wait();
            mutex.ReleaseMutex();
        });
        _thread.Start();
        ready.Wait();
    }

    public void Dispose()
    {
        _release.Set();
        _thread.Join();
        _release.Dispose();
    }
}
