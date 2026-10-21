using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BrookClient.Core;

namespace BrookClient.WindowsTests;

/// <summary>Renders the real window with the Retro theme; snapshots go to SNAPSHOT_DIR (uploaded by CI).</summary>
[Collection("windows")]
public sealed class UiTests(UiThread ui)
{
    private static readonly string SnapshotDir =
        Environment.GetEnvironmentVariable("SNAPSHOT_DIR") is { Length: > 0 } d ? d : Path.Combine(Path.GetTempPath(), "brookclient-snapshots");

    private static AppConfig SampleConfig() => new()
    {
        Profiles =
        [
            new Profile("Home HK", "brook://server?name=Home+HK&password=hello&server=hk.example.com%3A9999"),
            new Profile("Tokyo wss", "brook://wssserver?password=x&wssserver=wss%3A%2F%2Fexample.com%3A443%2Fws"),
            new Profile("Broken", "not a link"),
        ],
        SelectedIndex = 0,
    };

    [Theory]
    [InlineData(660, 620)]
    [InlineData(520, 440)] // minimum size
    [InlineData(1000, 760)]
    public void MainWindowRendersEveryTabWithoutClipping(int width, int height)
    {
        Directory.CreateDirectory(SnapshotDir);
        ui.Invoke(() =>
        {
            ui.App.InitializeForTests(SampleConfig(), new SystemProxy(new FakeStore(), Path.Combine(ui.DataDir, "ui-backup.json")));
            var w = new MainWindow { Width = width, Height = height, Left = 0, Top = 0, ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual };
            w.Show();
            try
            {
                var tabs = (TabControl)w.FindName("Tabs");
                for (var i = 0; i < tabs.Items.Count; i++)
                {
                    tabs.SelectedIndex = i;
                    w.UpdateLayout();
                    var header = ((TabItem)tabs.Items[i]).Header.ToString()!;
                    Save(w, $"main-{width}x{height}-{i}-{header}.png");
                    AssertInside(w, FindButton(w, "_Save"), header);
                    AssertInside(w, FindButton(w, "_Revert"), header);
                }

                // The broken link is flagged and blocks saving once something changed.
                var model = (SettingsViewModel)w.DataContext;
                Assert.Contains(model.Errors, e => e.Contains("Broken"));
                model.SocksPort = "abc";
                Assert.False(model.CanSave);
                Assert.Contains(model.Errors, e => e.Contains("not a number"));
            }
            finally
            {
                CloseDiscarding(w);
            }
        });
    }

    [Fact]
    public void EmptyStateShowsWithoutServers()
    {
        ui.Invoke(() =>
        {
            ui.App.InitializeForTests(new AppConfig(), new SystemProxy(new FakeStore(), Path.Combine(ui.DataDir, "ui-backup.json")));
            var w = new MainWindow { Width = 660, Height = 620, Left = 0, Top = 0, ShowActivated = false };
            w.Show();
            try
            {
                w.UpdateLayout();
                var empty = (FrameworkElement)w.FindName("EmptyServers");
                Assert.Equal(Visibility.Visible, empty.Visibility);
                Save(w, "main-empty.png");

                var model = (SettingsViewModel)w.DataContext;
                Assert.Equal(2, model.AddFromText("brook://server?name=A&password=x&server=1.2.3.4%3A1\nbrook://server?password=x&server=1.2.3.4%3A2"));
                w.UpdateLayout();
                Assert.Equal(Visibility.Collapsed, empty.Visibility);
                Assert.True(model.CanSave);
                Assert.Equal(0, model.ToConfig().SelectedIndex);
                Assert.Equal(["A", "Server 2"], model.ToConfig().Profiles.Select(p => p.Name));
                Save(w, "main-pasted.png");
            }
            finally
            {
                CloseDiscarding(w);
            }
        });
    }

    [Fact]
    public void TrayMenuListsServersAndActions()
    {
        ui.Invoke(() =>
        {
            ui.App.InitializeForTests(SampleConfig(), new SystemProxy(new FakeStore(), Path.Combine(ui.DataDir, "ui-backup.json")));
            using var tray = new TrayIcon(ui.App);
            tray.Rebuild();
            var headers = tray.Menu.Items.OfType<MenuItem>().Select(m => m.Header?.ToString()).ToList();
            Assert.Equal("Disconnected", headers[0]);
            Assert.Contains("_Connect", headers);
            Assert.Contains("E_xit", headers);
            var servers = tray.Menu.Items.OfType<MenuItem>().Single(m => (string)m.Header == "_Server");
            Assert.Equal(3, servers.Items.Count);
            Assert.True(((MenuItem)servers.Items[0]).IsChecked);
        });
    }

    /// <summary>Closing with unsaved edits asks "Save your changes?"; tests discard them first so no modal blocks.</summary>
    private void CloseDiscarding(MainWindow w)
    {
        ((SettingsViewModel)w.DataContext).Load(ui.App.Config);
        w.Close();
    }

    [Fact]
    public void ProxySettingsRoundTripThroughTheWindowAndAreEncrypted()
    {
        ui.Invoke(() =>
        {
            ui.App.InitializeForTests(SampleConfig(), new SystemProxy(new FakeStore(), Path.Combine(ui.DataDir, "ui-backup.json")));
            var w = new MainWindow { Width = 660, Height = 620, Left = 0, Top = 0, ShowActivated = false };
            w.Show();
            try
            {
                var model = (SettingsViewModel)w.DataContext;
                model.IsUpstreamManual = true;
                model.UpstreamHost = "proxy.corp";
                model.UpstreamPort = "3128";
                model.UpstreamUsername = "alice";
                ((PasswordBox)w.FindName("UpstreamPasswordBox")).Password = "s3cret";
                model.Profiles[2].Link = "brook://server?password=x&server=1.2.3.4%3A1"; // fix the broken sample
                var c = model.ToConfig();
                Assert.Equal(UpstreamProxyMode.Manual, c.UpstreamProxy);
                Assert.Equal(3128, c.UpstreamProxyPort);
                Assert.Equal("s3cret", c.UpstreamProxyPassword);
                Assert.Empty(model.Errors);

                var tabs = (TabControl)w.FindName("Tabs");
                tabs.SelectedIndex = 1;
                w.UpdateLayout();
                Save(w, "main-proxy-manual.png");

                model.UpstreamPort = "abc";
                Assert.Contains(model.Errors, e => e.Contains("Proxy port 'abc'"));

                // Saved with DPAPI: the file never holds the password in plain text, and it loads back.
                var store = new ConfigStore(Path.Combine(ui.DataDir, "dpapi-config.json"), new Platform.DpapiSecretProtector());
                store.Save(c);
                var json = File.ReadAllText(store.Path);
                Assert.DoesNotContain("s3cret", json);
                Assert.Contains("\"upstreamProxyPasswordData\": \"dpapi:", json);
                Assert.Equal("s3cret", store.Load().UpstreamProxyPassword);

                // Revert puts the saved (empty) password back into the box.
                model.Load(ui.App.Config);
                Assert.Equal("", ((PasswordBox)w.FindName("UpstreamPasswordBox")).Password);
            }
            finally
            {
                CloseDiscarding(w);
            }
        });
    }

    /// <summary>Opens the real tray menu and clicks its items: switch server (saved to disk), connect, disconnect.</summary>
    [Fact]
    public void TrayMenuOpensAndItsItemsWork()
    {
        ui.Invoke(() =>
        {
            var config = SampleConfig();
            config.Profiles[2] = new Profile("Local", "brook://server?password=pw&server=127.0.0.1%3A1"); // valid, unreachable
            ui.App.InitializeForTests(config, new SystemProxy(new FakeStore(), Path.Combine(ui.DataDir, "tray-backup.json")));
            using var tray = new TrayIcon(ui.App);
            tray.ShowMenu();
            Assert.True(tray.Menu.IsOpen);
            var root = tray.Menu.Items.OfType<MenuItem>().ToList();

            // Pick the second server from the Server submenu.
            var servers = root.Single(m => (string)m.Header == "_Server");
            ((MenuItem)servers.Items[1]).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Assert.Equal(1, ui.App.Config.SelectedIndex);
            Assert.Equal(1, ConfigStore.Standard.Load().SelectedIndex);
            tray.Menu.IsOpen = false;

            // Connect from the menu, then the rebuilt menu offers Disconnect.
            tray.ShowMenu();
            root = tray.Menu.Items.OfType<MenuItem>().ToList();
            root.Single(m => (string)m.Header == "_Connect").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Assert.True(ui.App.Engine.IsActive);
            tray.Menu.IsOpen = false;
            tray.ShowMenu();
            root = tray.Menu.Items.OfType<MenuItem>().ToList();
            Assert.StartsWith("Connecting", (string)root[0].Header);
            root.Single(m => (string)m.Header == "_Disconnect").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Assert.IsType<EngineState.Disconnected>(ui.App.Engine.State);
            tray.Menu.IsOpen = false;
        });
    }

    private static Button FindButton(DependencyObject root, string content)
    {
        foreach (var b in Descendants<Button>(root))
            if (b.Content as string == content) return b;
        throw new InvalidOperationException($"No button '{content}'");
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T t) yield return t;
            foreach (var d in Descendants<T>(child)) yield return d;
        }
    }

    private static void AssertInside(Window w, FrameworkElement e, string where)
    {
        var content = (FrameworkElement)w.Content;
        var topLeft = e.TranslatePoint(new Point(0, 0), content);
        Assert.True(e.ActualWidth > 0 && e.ActualHeight > 0, $"{where}: button not laid out");
        Assert.True(topLeft.X >= 0 && topLeft.Y >= 0 && topLeft.X + e.ActualWidth <= content.ActualWidth + 0.5
                    && topLeft.Y + e.ActualHeight <= content.ActualHeight + 0.5,
            $"{where}: button at {topLeft} size {e.ActualWidth}x{e.ActualHeight} is outside {content.ActualWidth}x{content.ActualHeight}");
    }

    private static void Save(Window w, string name)
    {
        var dpi = VisualTreeHelper.GetDpi(w);
        var bmp = new RenderTargetBitmap((int)(w.ActualWidth * dpi.DpiScaleX), (int)(w.ActualHeight * dpi.DpiScaleY),
            dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        bmp.Render(w);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using var f = File.Create(Path.Combine(SnapshotDir, name));
        enc.Save(f);
    }
}

internal sealed class FakeStore : IProxySettingsStore
{
    public ProxySettings Current = new(false, null, null, null);
    public ProxySettings Read() => Current;
    public void Write(ProxySettings s) => Current = s;
}
