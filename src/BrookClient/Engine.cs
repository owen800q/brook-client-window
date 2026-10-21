using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Text;
using System.Windows.Threading;
using BrookClient.Core;
using BrookClient.Platform;

namespace BrookClient;

public abstract record EngineState
{
    public sealed record Disconnected : EngineState;
    public sealed record Connecting(string What) : EngineState;
    public sealed record Connected(string Profile, string Detail) : EngineState;
    public sealed record Failed(string Message) : EngineState;
}

/// <summary>
/// Owns the brook process and the system proxy settings, and keeps them healthy: it reconnects when brook exits or
/// the server's address changes. All public members run on the UI thread. Nothing here needs administrator rights.
/// </summary>
internal sealed class Engine
{
    private sealed class Failure(string message, bool retryable = false) : Exception(message)
    {
        /// <summary>Network blips and server hiccups are retried; config problems are not.</summary>
        public bool Retryable { get; } = retryable;
        public static Failure Transient(string m) => new(m, true);
    }

    private sealed record Session(AppConfig Config, Process Process, List<string> ServerIPs, string PinnedIP, bool ProxyApplied);

    private readonly Dispatcher _ui;
    private readonly SystemProxy _proxy;
    private EngineState _state = new EngineState.Disconnected();
    private Session? _session;
    private AppConfig? _pendingConfig;
    private bool _wantConnected;
    private int _retries;
    private int _generation; // bumps to invalidate in-flight async work
    private DispatcherTimer? _retryTimer;
    private DispatcherTimer? _networkDebounce;

    public Engine(Dispatcher ui, SystemProxy proxy)
    {
        _ui = ui;
        _proxy = proxy;
        NetworkChange.NetworkAddressChanged += (_, _) => _ui.BeginInvoke(NetworkChanged);
    }

    public event Action<EngineState>? StateChanged;

    public EngineState State
    {
        get => _state;
        private set
        {
            if (value == _state) return;
            _state = value;
            Log.Write($"state: {value}");
            StateChanged?.Invoke(value);
        }
    }

    public bool IsActive => State is EngineState.Connecting or EngineState.Connected;

    public IReadOnlyList<string> ServerIPs => _session?.ServerIPs ?? [];

    internal int? BrookProcessId => _session?.Process.Id;

    public void Connect(AppConfig config)
    {
        _wantConnected = true;
        _retries = 0;
        Start(config.Clone());
    }

    public void Disconnect()
    {
        _wantConnected = false;
        _generation++;
        _retryTimer?.Stop();
        var old = _session;
        _session = null;
        State = new EngineState.Disconnected();
        Task.Run(() => TearDown(old));
    }

    /// <summary>Blocking teardown used when the app exits or Windows signs out.</summary>
    public void ShutdownSync()
    {
        _wantConnected = false;
        _generation++;
        _retryTimer?.Stop();
        TearDown(_session);
        _session = null;
    }

    // ---- Lifecycle -------------------------------------------------------------------------

    private void Start(AppConfig config)
    {
        _generation++;
        var gen = _generation;
        _retryTimer?.Stop();
        if (config.SelectedProfile is not { } profile)
        {
            State = new EngineState.Failed("No server selected");
            return;
        }
        _pendingConfig = config;
        State = new EngineState.Connecting(profile.Name);
        var old = _session;
        _session = null;
        Task.Run(() =>
        {
            TearDown(old, restoreProxy: false); // a reconnect replaces the proxy settings in place
            Session? s = null;
            Failure? f = null;
            try { s = BringUp(config, gen); }
            catch (Failure e) { f = e; }
            catch (Exception e) { f = new Failure($"Unexpected error: {e.Message}"); Log.Write(e.ToString()); }
            _ui.BeginInvoke(() => FinishStart(gen, config, s, f));
        });
    }

    private void FinishStart(int gen, AppConfig config, Session? s, Failure? failure)
    {
        if (gen != _generation)
        {
            // Superseded (user disconnected or reconnected meanwhile): undo what we just built,
            // but leave the proxy settings alone if a newer connection is (or soon will be) using them.
            if (s is not null) { var restore = !_wantConnected; Task.Run(() => TearDown(s, restore)); }
            return;
        }
        if (s is null)
        {
            Fail(failure ?? new Failure("Unknown error"));
            return;
        }
        _session = s;
        _retries = 0;
        if (s.Process.HasExited)
        {
            Fail(Failure.Transient($"brook exited (code {s.Process.ExitCode}); see log"));
            return;
        }
        var proxy = config.SetSystemProxy
            ? $"Windows proxy → 127.0.0.1:{config.HttpPort}"
            : $"Local proxies only: SOCKS5 127.0.0.1:{config.SocksPort}, HTTP 127.0.0.1:{config.HttpPort}";
        State = new EngineState.Connected(config.SelectedProfile?.Name ?? "?", $"{proxy} · server {s.PinnedIP} · LAN bypassed");
    }

    private void Fail(Failure failure)
    {
        var old = _session;
        _session = null;
        var retry = _wantConnected && failure.Retryable && _retries < RetryPolicy.MaxRetries && _pendingConfig is not null;
        // While retrying, keep our proxy in place so apps don't silently go direct; it is restored if we give up.
        Task.Run(() => TearDown(old, restoreProxy: !retry));
        if (retry)
        {
            _retries++;
            var delay = RetryPolicy.Delay(_retries);
            State = new EngineState.Connecting($"Retrying in {delay.TotalSeconds:0}s — {failure.Message}");
            var gen = _generation;
            var config = _pendingConfig!;
            _retryTimer?.Stop();
            _retryTimer = new DispatcherTimer(delay, DispatcherPriority.Normal, (_, _) =>
            {
                _retryTimer?.Stop();
                if (gen == _generation && _wantConnected) Start(config);
            }, _ui);
            _retryTimer.Start();
        }
        else
        {
            _wantConnected = false;
            if (old is null && _proxy.IsApplied) Task.Run(() => RestoreProxy());
            State = new EngineState.Failed(failure.Message);
        }
    }

    private void ProcessExited(Process process)
    {
        if (_session is not { } s || !ReferenceEquals(s.Process, process)) return;
        int code;
        try { code = process.ExitCode; } catch (InvalidOperationException) { code = -1; }
        Fail(Failure.Transient($"brook exited (code {code}); see log"));
    }

    /// <summary>Wi-Fi switch, cable plugged, VPN up...: if the server now resolves elsewhere, reconnect to the new address.</summary>
    private void NetworkChanged()
    {
        if (_session is null) return;
        _networkDebounce?.Stop();
        _networkDebounce = new DispatcherTimer(TimeSpan.FromSeconds(2), DispatcherPriority.Normal, (_, _) =>
        {
            _networkDebounce?.Stop();
            if (_session is not { } s) return;
            var gen = _generation;
            Task.Run(() =>
            {
                if (s.Config.SelectedProfile is not { } p || !BrookLink.TryParse(p.Link, out var link) || link!.DialHostIsIPv4) return;
                var ips = Net.ResolveIPv4(link.DialHost);
                if (ips.Count == 0 || ips.Contains(s.PinnedIP)) return;
                _ui.BeginInvoke(() =>
                {
                    if (gen != _generation || !_wantConnected) return;
                    Log.Write($"server address changed ({s.PinnedIP} → {string.Join(", ", ips)}); reconnecting");
                    _retries = 0;
                    Start(s.Config);
                });
            });
        }, _ui);
        _networkDebounce.Start();
    }

    // ---- Blocking work (background) ------------------------------------------------------

    private Session BringUp(AppConfig config, int gen)
    {
        var profile = config.SelectedProfile ?? throw new Failure("No server selected");
        if (!BrookLink.TryParse(profile.Link, out var link)) throw new Failure($"Invalid brook link for '{profile.Name}'");
        var problems = config.ValidationErrors();
        if (problems.Count > 0) throw new Failure(string.Join("; ", problems));
        var brookPath = AppPaths.Brook ?? throw new Failure("brook.exe not found next to BrookClient.exe");

        var ips = Net.ResolveIPv4(link!.DialHost);
        if (ips.Count == 0) throw Failure.Transient($"Cannot resolve {link.DialHost}");
        var pinIP = ips[0];

        foreach (var port in new[] { config.SocksPort, config.HttpPort })
            if (Net.PortInUse(port)) throw new Failure($"Port {port} is already in use by another program");

        var process = StartBrook(brookPath, BrookCommand.Arguments(link, pinIP, config));
        try
        {
            var deadline = DateTime.UtcNow.AddSeconds(8);
            while (!(Net.AcceptsConnections(config.SocksPort) && Net.AcceptsConnections(config.HttpPort)))
            {
                if (process.HasExited) throw Failure.Transient($"brook failed to start: {LogTail()}");
                if (DateTime.UtcNow > deadline) throw Failure.Transient("brook did not open its local proxy");
                if (gen != _generation) throw new Failure("superseded");
                Thread.Sleep(100);
            }
            var proxyApplied = false;
            if (config.SetSystemProxy)
            {
                _proxy.Apply(new ProxyPlan(config.HttpPort, config.BypassCidrs));
                proxyApplied = true;
            }
            else if (_proxy.IsApplied)
            {
                RestoreProxy(); // switched from "set Windows proxy" to "local proxies only"
            }
            return new Session(config, process, ips, pinIP, proxyApplied);
        }
        catch
        {
            Kill(process);
            throw;
        }
    }

    private Process StartBrook(string path, IReadOnlyList<string> args)
    {
        var psi = new ProcessStartInfo(path)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            WorkingDirectory = Path.GetDirectoryName(path)!,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        var log = OpenLog();
        var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        DataReceivedEventHandler write = (_, e) =>
        {
            if (e.Data is null) return;
            lock (log) { try { log.WriteLine(e.Data); } catch (ObjectDisposedException) { } }
        };
        process.OutputDataReceived += write;
        process.ErrorDataReceived += write;
        process.Exited += (_, _) =>
        {
            lock (log) log.Dispose();
            _ui.BeginInvoke(() => ProcessExited(process));
        };
        try
        {
            if (!process.Start()) throw new Failure("Cannot start brook");
        }
        catch (System.ComponentModel.Win32Exception e)
        {
            lock (log) log.Dispose();
            throw new Failure($"Cannot start brook: {e.Message}");
        }
        try { Native.KillWithUs(process); }
        catch (System.ComponentModel.Win32Exception e) { Log.Write($"job object: {e.Message}"); } // still works, just no crash-kill
        process.StandardInput.Close();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        Log.Write($"started brook (pid {process.Id})");
        return process;
    }

    private void TearDown(Session? s, bool restoreProxy = true)
    {
        // Proxy first, so apps never point at a proxy that is gone.
        if (restoreProxy) RestoreProxy();
        if (s is not null) Kill(s.Process);
    }

    private void RestoreProxy()
    {
        try { if (_proxy.IsApplied) _proxy.Restore(); }
        catch (Exception e) { Log.Write($"restoring proxy settings failed: {e.Message}"); }
    }

    private static void Kill(Process p)
    {
        try
        {
            if (!p.HasExited)
            {
                p.EnableRaisingEvents = false;
                p.Kill(entireProcessTree: true);
                p.WaitForExit(3000);
            }
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // Already gone.
        }
    }

    private static StreamWriter OpenLog()
    {
        var path = Paths.BrookLog;
        try
        {
            if (File.Exists(path) && new FileInfo(path).Length > 1_000_000) File.Delete(path);
            var w = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete), new UTF8Encoding(false))
            {
                AutoFlush = true,
            };
            w.WriteLine($"--- {DateTime.Now:yyyy-MM-dd HH:mm:ss} brook connect");
            return w;
        }
        catch (IOException)
        {
            return StreamWriter.Null;
        }
    }

    private static string LogTail()
    {
        try
        {
            using var fs = new FileStream(Paths.BrookLog, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(fs);
            var lines = reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(l => !l.StartsWith("---", StringComparison.Ordinal)).TakeLast(2).ToList();
            var text = string.Join(" | ", lines);
            return text.Length == 0 ? "no output" : text.Length > 300 ? text[^300..] : text;
        }
        catch (IOException)
        {
            return "no output";
        }
    }
}

/// <summary>Files shipped next to the exe.</summary>
internal static class AppPaths
{
    public static string AppDir => AppContext.BaseDirectory;

    public static string? Brook
    {
        get
        {
            var candidates = new[] { Path.Combine(AppDir, "brook.exe"), Path.Combine(AppDir, "bin", "brook.exe") };
            return candidates.FirstOrDefault(File.Exists);
        }
    }
}
