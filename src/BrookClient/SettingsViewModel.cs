using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using BrookClient.Core;

namespace BrookClient;

public abstract class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Raise(name);
        return true;
    }

    protected void Raise([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class ProfileRow : Observable
{
    private readonly Action _changed;
    private string _name;
    private string _link;
    private bool _isSelected;

    public ProfileRow(Profile p, bool selected, Action changed)
    {
        _name = p.Name;
        _link = p.Link;
        _isSelected = selected;
        _changed = changed;
    }

    public string Name { get => _name; set { if (Set(ref _name, value)) _changed(); } }
    public string Link { get => _link; set { if (Set(ref _link, value)) { Raise(nameof(LinkError)); _changed(); } } }
    public bool IsSelected { get => _isSelected; set { if (Set(ref _isSelected, value)) _changed(); } }

    /// <summary>Shown under the link field (and tints it) when the link does not parse.</summary>
    public string? LinkError =>
        !BrookLink.TryParse(Link, out var l) ? "Not a valid brook:// link"
        : l!.Kind == BrookLinkKind.Socks5 ? "socks5 links are not supported by brook connect"
        : null;
}

/// <summary>The editable copy of the settings shown in the main window. Nothing is applied until Save.</summary>
public sealed class SettingsViewModel : Observable
{
    private AppConfig _saved;
    private string _socksPort = "";
    private string _httpPort = "";
    private string _bypassText = "";
    private bool _connectOnLaunch;
    private bool _udpOverTcp;
    private bool _setSystemProxy;
    private List<string> _errors = [];
    private bool _isDirty;
    private bool _loading;

    public SettingsViewModel(AppConfig config)
    {
        _saved = config;
        Profiles.CollectionChanged += (_, _) => Changed();
        Load(config);
    }

    public ObservableCollection<ProfileRow> Profiles { get; } = [];

    public string SocksPort { get => _socksPort; set { if (Set(ref _socksPort, value)) Changed(); } }
    public string HttpPort { get => _httpPort; set { if (Set(ref _httpPort, value)) Changed(); } }
    public string BypassText { get => _bypassText; set { if (Set(ref _bypassText, value)) Changed(); } }
    public bool ConnectOnLaunch { get => _connectOnLaunch; set { if (Set(ref _connectOnLaunch, value)) Changed(); } }
    public bool UdpOverTcp { get => _udpOverTcp; set { if (Set(ref _udpOverTcp, value)) Changed(); } }
    public bool SetSystemProxy { get => _setSystemProxy; set { if (Set(ref _setSystemProxy, value)) Changed(); } }

    public IReadOnlyList<string> Errors => _errors;
    public string? FirstError => _errors.Count == 0 ? null : _errors.Count == 1 ? _errors[0] : $"{_errors[0]} (+{_errors.Count - 1} more)";
    public bool HasErrors => _errors.Count > 0;
    public bool IsDirty { get => _isDirty; private set => Set(ref _isDirty, value); }
    public bool CanSave => IsDirty && !HasErrors;
    public bool HasProfiles => Profiles.Count > 0;

    public void Load(AppConfig c)
    {
        _loading = true;
        _saved = c.Clone();
        Profiles.Clear();
        for (var i = 0; i < c.Profiles.Count; i++) Profiles.Add(new ProfileRow(c.Profiles[i], c.SelectedIndex == i, Changed));
        SocksPort = c.SocksPort.ToString();
        HttpPort = c.HttpPort.ToString();
        BypassText = string.Join(Environment.NewLine, c.BypassCidrs);
        ConnectOnLaunch = c.ConnectOnLaunch;
        UdpOverTcp = c.UdpOverTcp;
        SetSystemProxy = c.SetSystemProxy;
        _loading = false;
        Changed();
    }

    public AppConfig ToConfig()
    {
        var c = new AppConfig
        {
            Profiles = Profiles.Select(p => new Profile(p.Name.Trim(), p.Link.Trim())).ToList(),
            BypassCidrs = Tokens(BypassText),
            SocksPort = ParsePort(SocksPort),
            HttpPort = ParsePort(HttpPort),
            ConnectOnLaunch = ConnectOnLaunch,
            UdpOverTcp = UdpOverTcp,
            SetSystemProxy = SetSystemProxy,
        };
        var selected = Profiles.ToList().FindIndex(p => p.IsSelected);
        c.SelectedIndex = selected >= 0 ? selected : c.Profiles.Count > 0 ? 0 : null;
        return c;
    }

    /// <summary>Adds every brook:// link found in <paramref name="text"/>. Returns how many were added.</summary>
    public int AddFromText(string? text)
    {
        var added = LinkImport.Profiles(text, Profiles.Count);
        var noneSelected = !Profiles.Any(p => p.IsSelected);
        foreach (var p in added) Profiles.Add(new ProfileRow(p, false, Changed));
        if (noneSelected && added.Count > 0) Profiles[Profiles.Count - added.Count].IsSelected = true;
        return added.Count;
    }

    public void AddBlank() => Profiles.Add(new ProfileRow(new Profile($"Server {Profiles.Count + 1}", ""), !Profiles.Any(p => p.IsSelected), Changed));

    public void Remove(ProfileRow row)
    {
        var wasSelected = row.IsSelected;
        Profiles.Remove(row);
        if (wasSelected && Profiles.Count > 0) Profiles[0].IsSelected = true;
    }

    public void Select(ProfileRow row)
    {
        foreach (var p in Profiles) p.IsSelected = ReferenceEquals(p, row);
    }

    public void ResetBypass() => BypassText = string.Join(Environment.NewLine, AppConfig.DefaultBypass);

    public void MarkSaved(AppConfig c)
    {
        _saved = c.Clone();
        Changed();
    }

    private void Changed()
    {
        if (_loading) return;
        var c = ToConfig();
        var errors = new List<string>();
        if (!IsNumber(SocksPort)) errors.Add($"SOCKS5 port '{SocksPort}' is not a number");
        if (!IsNumber(HttpPort)) errors.Add($"HTTP port '{HttpPort}' is not a number");
        // Unparsable ports are already reported above.
        errors.AddRange(c.ValidationErrors().Where(e =>
            !(e == "SOCKS5 port 0 is out of range" && !IsNumber(SocksPort)) && !(e == "HTTP port 0 is out of range" && !IsNumber(HttpPort))
            && !(e.Contains("must differ") && (!IsNumber(SocksPort) || !IsNumber(HttpPort)))));
        _errors = errors;
        IsDirty = !c.Equals(_saved);
        Raise(nameof(Errors));
        Raise(nameof(FirstError));
        Raise(nameof(HasErrors));
        Raise(nameof(CanSave));
        Raise(nameof(HasProfiles));
    }

    /// <summary>Unparsable text becomes 0, which validation reports once as "not a number".</summary>
    private static int ParsePort(string s) => int.TryParse(s.Trim(), out var p) ? p : 0;

    private static bool IsNumber(string s) => int.TryParse(s.Trim(), out _);

    private static List<string> Tokens(string s) =>
        s.Split([' ', '\t', '\r', '\n', ',', ';'], StringSplitOptions.RemoveEmptyEntries).ToList();
}
