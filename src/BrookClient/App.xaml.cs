using System.Diagnostics;
using System.Windows;
using BrookClient.Core;
using BrookClient.Platform;
using Retro.Wpf.Controls;

namespace BrookClient;

public partial class App : Application
{
    private const string InstanceMutexName = @"Local\BrookClient.SingleInstance";
    private const string ShowEventName = @"Local\BrookClient.Show";

    private Mutex? _instanceMutex;
    private EventWaitHandle? _showEvent;
    private readonly ConfigStore _store = ConfigStore.Standard;
    private SystemProxy _proxy = null!;
    private TrayIcon? _tray;
    private MainWindow? _window;

    internal AppConfig Config { get; private set; } = new();
    internal Engine Engine { get; private set; } = null!;

    /// <summary>Raised after the saved config changed (from the window or the tray menu).</summary>
    internal event Action? ConfigChanged;

    internal static new App Current => (App)Application.Current;

    [STAThread]
    public static int Main(string[] args)
    {
        // `BrookClient.exe --watchdog <pid>`: restores the proxy settings if the app dies without cleaning up.
        if (args.Length == 2 && args[0] == "--watchdog" && int.TryParse(args[1], out var pid))
            return Watchdog.Run(pid);

        var app = new App();
        app.InitializeComponent();
        // Wired here rather than in OnStartup, so hosting App elsewhere (the Windows tests) never runs the real startup.
        app.Startup += (_, e) => app.StartUp(e);
        return app.Run();
    }

    private void StartUp(StartupEventArgs e)
    {
        Log.Write($"starting {typeof(App).Assembly.GetName().Version} (pid {Environment.ProcessId}, args: {string.Join(" ", e.Args)})");

        _instanceMutex = new Mutex(true, InstanceMutexName, out var first);
        if (!first)
        {
            // Already running: bring its window up instead.
            Log.Write("another instance is running; asking it to show its window");
            try { EventWaitHandle.OpenExisting(ShowEventName).Set(); } catch (WaitHandleCannotBeOpenedException) { }
            Shutdown();
            return;
        }
        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        new Thread(() =>
        {
            while (_showEvent.WaitOne()) Dispatcher.BeginInvoke(ShowMainWindow);
        }) { IsBackground = true, Name = "show-signal" }.Start();

        DispatcherUnhandledException += (_, ex) =>
        {
            Log.Write($"unhandled: {ex.Exception}");
            RetroMessageBox.Show(ex.Exception.Message, "Brook Client error", MessageBoxButton.OK, MessageBoxImage.Error);
            ex.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, ex) =>
        {
            Log.Write($"fatal: {ex.ExceptionObject}");
            try { _proxy.Restore(); } catch { /* best effort */ }
        };
        SessionEnding += (_, _) => Engine.ShutdownSync();

        _proxy = new SystemProxy(new RegistryProxyStore(), Paths.ProxyBackup);
        // Clean up after a previous crash the watchdog could not handle (e.g. power loss).
        if (_proxy.IsApplied)
        {
            Log.Write("restoring proxy settings left over from a previous run");
            try { _proxy.Restore(); } catch (Exception ex) { Log.Write($"restore failed: {ex.Message}"); }
        }

        try { Config = _store.Load(); }
        catch (Exception ex)
        {
            RetroMessageBox.Show($"Could not read settings ({ex.Message}).\nUsing defaults.", "Brook Client", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        Engine = new Engine(Dispatcher, _proxy);
        Engine.StateChanged += _ => _tray?.Update();
        StartWatchdog();
        _tray = new TrayIcon(this);

        var autostart = e.Args.Contains(Autostart.Flag);
        if (Config.Profiles.Count == 0 || !autostart) ShowMainWindow();
        if (Config.ConnectOnLaunch && Config.SelectedProfile is not null) Engine.Connect(Config);
    }

    /// <summary>Sets up the engine without the tray, single-instance lock or watchdog (Windows tests).</summary>
    internal void InitializeForTests(AppConfig config, SystemProxy proxy)
    {
        _proxy = proxy;
        Config = config;
        Engine = new Engine(Dispatcher, proxy);
    }

    // ---- Actions shared by the window and the tray -------------------------------------------

    internal void ShowMainWindow()
    {
        if (_window is null)
        {
            _window = new MainWindow();
            _window.Closed += (_, _) => _window = null; // free the window between uses to keep memory low
            _window.Show();
        }
        if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
        _window.Activate();
    }

    internal void ShowTrayHint() => _tray?.ShowStillRunningHint();

    internal void ToggleConnection()
    {
        if (Engine.IsActive) Engine.Disconnect(); else Engine.Connect(Config);
    }

    internal void SelectProfile(int index)
    {
        var c = Config.Clone();
        c.SelectedIndex = index;
        SaveConfig(c);
    }

    /// <summary>Saves and applies new settings; reconnects when connected. Returns false if saving failed.</summary>
    internal bool SaveConfig(AppConfig config)
    {
        try
        {
            _store.Save(config);
        }
        catch (Exception ex)
        {
            RetroMessageBox.Show($"Could not save settings: {ex.Message}", "Brook Client", MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
        Config = config;
        ConfigChanged?.Invoke();
        _tray?.Update();
        if (Engine.IsActive) Engine.Connect(Config);
        return true;
    }

    internal static void OpenFile(string path)
    {
        try
        {
            if (!File.Exists(path)) File.WriteAllText(path, "");
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            RetroMessageBox.Show($"Cannot open {path}: {ex.Message}", "Brook Client", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    internal static void OpenFolder(string path) =>
        Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { path }, UseShellExecute = false });

    internal void Quit()
    {
        if (_window is not null)
        {
            _window.Close(); // may ask to save unsaved settings
            if (_window is not null) return; // the user chose Cancel: keep running
        }
        Engine.ShutdownSync();
        _tray?.Dispose();
        _tray = null;
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        _instanceMutex?.Dispose();
        base.OnExit(e);
    }

    private static void StartWatchdog()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (exe is null || !exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return;
            var psi = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true };
            psi.ArgumentList.Add("--watchdog");
            psi.ArgumentList.Add(Environment.ProcessId.ToString());
            Process.Start(psi)?.Dispose();
        }
        catch (Exception ex)
        {
            Log.Write($"watchdog not started: {ex.Message}");
        }
    }
}

/// <summary>
/// A copy of the app that waits for the main process to exit. If it exits without restoring the proxy settings
/// (crash, killed from Task Manager), the watchdog restores them so the user is not left without internet.
/// brook itself dies with the app through a job object.
/// </summary>
internal static class Watchdog
{
    public static int Run(int pid)
    {
        try
        {
            using (var parent = Process.GetProcessById(pid)) parent.WaitForExit();
        }
        catch (ArgumentException)
        {
            // Already gone.
        }
        try
        {
            var proxy = new SystemProxy(new RegistryProxyStore(), Paths.ProxyBackup);
            if (proxy.IsApplied)
            {
                Log.Write("watchdog: app exited without cleanup; restoring proxy settings");
                proxy.Restore();
            }
        }
        catch (Exception e)
        {
            Log.Write($"watchdog: {e.Message}");
            return 1;
        }
        return 0;
    }
}
