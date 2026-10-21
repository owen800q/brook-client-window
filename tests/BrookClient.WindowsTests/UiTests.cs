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
                w.Close();
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
                w.Close();
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
