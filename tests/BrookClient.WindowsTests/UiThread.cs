using System.Windows.Threading;
using BrookClient.Core;

namespace BrookClient.WindowsTests;

/// <summary>One WPF Application per process: a dedicated STA thread runs it for every test in the "windows" collection.</summary>
public sealed class UiThread : IDisposable
{
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _ready = new();

    public string DataDir { get; } = Path.Combine(Path.GetTempPath(), "brookclient-wintest-" + Guid.NewGuid());
    internal App App { get; private set; } = null!;
    public Dispatcher Dispatcher { get; private set; } = null!;

    public UiThread()
    {
        Directory.CreateDirectory(DataDir);
        Paths.RootOverride = DataDir; // never touch the real user's config or logs
        _thread = new Thread(() =>
        {
            App = new App();
            App.InitializeComponent();
            Dispatcher = Dispatcher.CurrentDispatcher;
            _ready.Set();
            Dispatcher.Run();
        });
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.IsBackground = true;
        _thread.Start();
        _ready.Wait();
    }

    public T Invoke<T>(Func<T> f) => Dispatcher.Invoke(f);
    public void Invoke(Action a) => Dispatcher.Invoke(a);

    /// <summary>Polls <paramref name="condition"/> on the UI thread until it holds.</summary>
    public void WaitFor(Func<bool> condition, TimeSpan timeout, Func<string> describe)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!Invoke(condition))
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException(Invoke(describe));
            Thread.Sleep(100);
        }
    }

    public void Dispose()
    {
        Dispatcher.InvokeShutdown();
        _thread.Join(5000);
    }
}

[CollectionDefinition("windows", DisableParallelization = true)]
public sealed class WindowsCollection : ICollectionFixture<UiThread>;

/// <summary>Snapshot of the user's Internet Settings, so tests that touch the real registry put it back.</summary>
internal sealed class RegistryGuard : IDisposable
{
    private readonly Platform.RegistryProxyStore _store = new();

    public ProxySettings Original { get; }

    public RegistryGuard() => Original = _store.Read();

    public ProxySettings Current => _store.Read();

    public void Dispose() => _store.Write(Original);
}
