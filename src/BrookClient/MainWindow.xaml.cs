using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using BrookClient.Core;
using BrookClient.Platform;
using Retro.Wpf.Controls;

namespace BrookClient;

/// <summary>Status and settings. Closing it keeps the app running in the notification area.</summary>
public partial class MainWindow : RetroWindow
{
    public static readonly RoutedCommand SaveCommand = new();

    private static bool _hintShown;
    private readonly App _app = App.Current;
    private readonly SettingsViewModel _model;

    public MainWindow()
    {
        InitializeComponent();
        _model = new SettingsViewModel(_app.Config);
        DataContext = _model;
        ConfigPath.Text = ConfigStore.Standard.Path;
        LogPath.Text = Paths.BrookLog;
        LaunchAtLogin.IsChecked = SafeIsLoginItem();
        if (_app.Config.Profiles.Count == 0) Tabs.SelectedIndex = 0;

        _app.Engine.StateChanged += OnStateChanged;
        _app.ConfigChanged += OnConfigChanged;
        Closed += (_, _) =>
        {
            _app.Engine.StateChanged -= OnStateChanged;
            _app.ConfigChanged -= OnConfigChanged;
        };
        ShowState(_app.Engine.State);
    }

    private void OnStateChanged(EngineState s) => ShowState(s);

    /// <summary>The tray menu changed the server: pick it up unless the user is editing.</summary>
    private void OnConfigChanged()
    {
        if (!_model.IsDirty) _model.Load(_app.Config);
        ShowState(_app.Engine.State);
    }

    private void ShowState(EngineState state)
    {
        StatusLine.Text = StatusText.Line(state);
        var detail = state switch
        {
            EngineState.Connected c => c.Detail,
            EngineState.Disconnected when _app.Config.SelectedProfile is { } p => $"Selected server: {p.Name}. Click Connect.",
            EngineState.Disconnected => "Add a server on the Servers tab, save, then click Connect.",
            _ => null,
        };
        StatusDetail.Text = detail ?? "";
        StatusDetail.Visibility = detail is null ? Visibility.Collapsed : Visibility.Visible;
        Status.Severity = state switch
        {
            EngineState.Connected => MessageSeverity.Success,
            EngineState.Connecting => MessageSeverity.Warning,
            EngineState.Failed => MessageSeverity.Error,
            _ => MessageSeverity.Info,
        };
        StatusBarText.Text = StatusText.Line(state);

        var active = _app.Engine.IsActive;
        var canConnect = !active && _app.Config.SelectedProfile is not null;
        ConnectButton.IsEnabled = ConnectMenu.IsEnabled = canConnect;
        DisconnectButton.IsEnabled = DisconnectMenu.IsEnabled = active;
    }

    // ---- Connection ----------------------------------------------------------------------

    private void Connect_Click(object sender, RoutedEventArgs e)
    {
        if (_model.IsDirty)
        {
            if (_model.HasErrors)
            {
                RetroMessageBox.Show(this, "Fix the settings first:\n\n" + string.Join("\n", _model.Errors), "Brook Client",
                    MessageBoxButton.OK, MessageBoxImage.Warning, MessageBoxResult.OK);
                return;
            }
            if (!SaveNow()) return; // connect with what the user sees
        }
        _app.Engine.Connect(_app.Config);
    }

    private void Disconnect_Click(object sender, RoutedEventArgs e) => _app.Engine.Disconnect();

    // ---- Settings --------------------------------------------------------------------------

    private void Save_Click(object sender, RoutedEventArgs e) => SaveNow();

    private bool SaveNow()
    {
        if (!_model.CanSave) return !_model.IsDirty;
        var config = _model.ToConfig();
        if (!_app.SaveConfig(config)) return false;
        _model.Load(config);
        StatusBarText.Text = _app.Engine.IsActive ? "Saved. Reconnecting with the new settings…" : "Saved.";
        return true;
    }

    private void Revert_Click(object sender, RoutedEventArgs e) => _model.Load(_app.Config);

    private void Paste_Click(object sender, RoutedEventArgs e)
    {
        string? text = null;
        try { text = Clipboard.ContainsText() ? Clipboard.GetText() : null; }
        catch (System.Runtime.InteropServices.ExternalException) { /* clipboard busy */ }
        var added = _model.AddFromText(text);
        Tabs.SelectedIndex = 0;
        StatusBarText.Text = added switch
        {
            0 => "No brook:// link on the clipboard. Copy one first.",
            1 => "Added 1 server. Click Save to keep it.",
            _ => $"Added {added} servers. Click Save to keep them.",
        };
    }

    private void AddBlank_Click(object sender, RoutedEventArgs e)
    {
        _model.AddBlank();
        Tabs.SelectedIndex = 0;
    }

    private void RemoveServer_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is ProfileRow row) _model.Remove(row);
    }

    private void ResetBypass_Click(object sender, RoutedEventArgs e) => _model.ResetBypass();

    private void LaunchAtLogin_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            LoginItem.SetEnabled(LaunchAtLogin.IsChecked == true);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            RetroMessageBox.Show(this, $"Could not change the startup setting: {ex.Message}", "Brook Client",
                MessageBoxButton.OK, MessageBoxImage.Warning, MessageBoxResult.OK);
        }
        LaunchAtLogin.IsChecked = SafeIsLoginItem();
    }

    private static bool SafeIsLoginItem()
    {
        try { return LoginItem.IsEnabled; }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException) { return false; }
    }

    // ---- Menu --------------------------------------------------------------------------------

    private void OpenLog_Click(object sender, RoutedEventArgs e) => App.OpenFile(Paths.BrookLog);
    private void OpenClientLog_Click(object sender, RoutedEventArgs e) => App.OpenFile(Paths.AppLog);
    private void OpenFolder_Click(object sender, RoutedEventArgs e) => App.OpenFolder(Paths.AppData);
    private void CloseWindow_Click(object sender, RoutedEventArgs e) => Close();

    private void Exit_Click(object sender, RoutedEventArgs e) => _app.Quit(); // closing asks about unsaved changes

    private void About_Click(object sender, RoutedEventArgs e)
    {
        var version = typeof(MainWindow).Assembly.GetName().Version?.ToString(3) ?? "?";
        RetroMessageBox.Show(this,
            $"Brook Client {version} for Windows\n\n" +
            "Connects to a Brook server and points the Windows proxy at it, while your home network (LAN) stays direct.\n\n" +
            "Runs entirely as your user: no administrator rights, drivers or services.\n\n" +
            "https://github.com/owen800q/brook-client-window",
            "About Brook Client", MessageBoxButton.OK, MessageBoxImage.Information, MessageBoxResult.OK);
    }

    // ---- Closing -------------------------------------------------------------------------------

    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (e.Cancel) return;
        if (!ConfirmDiscard()) { e.Cancel = true; return; }
        if (!_hintShown)
        {
            _hintShown = true;
            _app.ShowTrayHint();
        }
    }

    /// <summary>Asks what to do with unsaved changes. False = stay.</summary>
    private bool ConfirmDiscard()
    {
        if (!_model.IsDirty) return true;
        var answer = RetroMessageBox.Show(this, "Save your changes to the settings?", "Brook Client",
            MessageBoxButton.YesNoCancel, MessageBoxImage.Question, MessageBoxResult.Yes);
        return answer switch
        {
            MessageBoxResult.Yes => SaveNow() || ShowSaveBlocked(),
            MessageBoxResult.No => true,
            _ => false,
        };
    }

    private bool ShowSaveBlocked()
    {
        if (_model.HasErrors)
            RetroMessageBox.Show(this, "The settings can't be saved yet:\n\n" + string.Join("\n", _model.Errors), "Brook Client",
                MessageBoxButton.OK, MessageBoxImage.Warning, MessageBoxResult.OK);
        return false;
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape && Keyboard.Modifiers == ModifierKeys.None) { Close(); e.Handled = true; return; }
        base.OnPreviewKeyDown(e);
    }
}
