using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using BrookClient.Platform;
using Retro.Wpf;
using Retro.Wpf.Controls;
using Forms = System.Windows.Forms;

namespace BrookClient;

/// <summary>The notification-area icon. Its menu is a WPF ContextMenu, so it gets the Retro look.</summary>
internal sealed class TrayIcon : IDisposable
{
    private readonly App _app;
    private readonly Forms.NotifyIcon _icon;
    private readonly ContextMenu _menu = new();
    private readonly Dictionary<string, System.Drawing.Icon> _icons = [];

    public TrayIcon(App app)
    {
        _app = app;
        _icon = new Forms.NotifyIcon { Visible = true };
        _icon.MouseUp += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Right) ShowMenu();
        };
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left) _app.ShowMainWindow();
        };
        Update();
    }

    public void Update()
    {
        var state = _app.Engine.State;
        _icon.Icon = Load(state switch
        {
            EngineState.Connected => "tray-on",
            EngineState.Connecting => "tray-busy",
            EngineState.Failed => "tray-error",
            _ => "tray-off",
        });
        var tip = "Brook Client — " + StatusText.Line(state);
        _icon.Text = tip.Length > 127 ? tip[..124] + "..." : tip; // Windows limit
    }

    /// <summary>Shown once, the first time the window is closed, so the user knows the app is still running.</summary>
    public void ShowStillRunningHint() =>
        _icon.ShowBalloonTip(4000, "Brook Client is still running",
            "It stays in the notification area. Right-click the shield icon to connect, disconnect or quit.", Forms.ToolTipIcon.Info);

    private void ShowMenu()
    {
        Rebuild();
        _menu.Placement = PlacementMode.MousePoint;
        _menu.IsOpen = true;
        // Without this the menu would not close when the user clicks elsewhere.
        if (PresentationSource.FromVisual(_menu) is HwndSource source) Native.SetForegroundWindow(source.Handle);
    }

    internal ContextMenu Menu => _menu;

    internal void Rebuild()
    {
        var engine = _app.Engine;
        var config = _app.Config;
        _menu.Items.Clear();
        _menu.Items.Add(Disabled(StatusText.Line(engine.State)));
        if (engine.State is EngineState.Connected c) _menu.Items.Add(Disabled("  " + c.Detail));
        _menu.Items.Add(new Separator());

        if (engine.IsActive)
            _menu.Items.Add(Item("_Disconnect", IconKind.Close, () => engine.Disconnect()));
        else
        {
            var connect = Item("_Connect", IconKind.Execute, () => engine.Connect(_app.Config));
            connect.IsEnabled = config.SelectedProfile is not null;
            _menu.Items.Add(connect);
        }

        var servers = new MenuItem { Header = "_Server" };
        if (config.Profiles.Count == 0) servers.Items.Add(Disabled("No servers — open Brook Client"));
        for (var i = 0; i < config.Profiles.Count; i++)
        {
            var index = i;
            var it = new MenuItem { Header = Escape(config.Profiles[i].Name), IsCheckable = true, IsChecked = config.SelectedIndex == i };
            it.Click += (_, _) => _app.SelectProfile(index);
            servers.Items.Add(it);
        }
        _menu.Items.Add(servers);

        _menu.Items.Add(new Separator());
        var open = Item("_Open Brook Client", IconKind.Settings, _app.ShowMainWindow);
        open.FontWeight = FontWeights.Bold;
        _menu.Items.Add(open);
        _menu.Items.Add(Item("Open _Log", IconKind.Document, () => App.OpenFile(Core.Paths.BrookLog)));
        _menu.Items.Add(new Separator());
        _menu.Items.Add(Item("E_xit", null, _app.Quit));
    }

    private static MenuItem Item(string header, IconKind? icon, Action action)
    {
        var it = new MenuItem { Header = header };
        if (icon is { } k) Assist.SetIcon(it, k);
        it.Click += (_, _) => action();
        return it;
    }

    private static MenuItem Disabled(string header) => new() { Header = Escape(header), IsEnabled = false };

    /// <summary>Menu headers treat '_' as an access key marker.</summary>
    private static string Escape(string s) => s.Replace("_", "__");

    private System.Drawing.Icon Load(string name)
    {
        if (_icons.TryGetValue(name, out var cached)) return cached;
        var info = Application.GetResourceStream(new Uri($"pack://application:,,,/BrookClient;component/Assets/{name}.ico"))!;
        using var stream = info.Stream;
        var icon = new System.Drawing.Icon(stream, Forms.SystemInformation.SmallIconSize);
        _icons[name] = icon;
        return icon;
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        foreach (var i in _icons.Values) i.Dispose();
        _icons.Clear();
    }
}

internal static class StatusText
{
    public static string Line(EngineState s) => s switch
    {
        EngineState.Connecting c => $"Connecting: {c.What}",
        EngineState.Connected c => $"Connected — {c.Profile}",
        EngineState.Failed f => $"Error: {f.Message}",
        _ => "Disconnected",
    };
}
