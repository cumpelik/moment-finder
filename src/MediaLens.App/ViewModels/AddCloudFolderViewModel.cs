using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MediaLens.Cloud;

namespace MediaLens.App.ViewModels;

/// <summary>
/// A folder in a storage (photos put there by other means: the storage's own app, another program) added to the
/// library as a source: choose the storage, go into the folder, add it, read-only if wished.
/// </summary>
public sealed partial class AddCloudFolderViewModel : CloudWorkViewModel
{
    private readonly LibraryHost _host;

    public ObservableCollection<CloudChoice> Destinations { get; } = [];
    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsNewStorage))] private CloudChoice? _destination;
    public bool IsNewStorage => Destination?.Cloud == null;
    public bool HasClouds { get; }
    public StorageFormViewModel Form { get; }

    // ---- going through the storage's folders ----
    private CloudInfo? _cloud;
    private string _root = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsBrowsing))] private bool _connected;
    public bool IsBrowsing => Connected;
    /// <summary>The folder shown, inside the storage ("" is its top).</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanGoUp), nameof(PlaceText), nameof(CanAdd), nameof(AddText))] private string _path = "";
    public ObservableCollection<StorageFolder> Folders { get; } = [];
    [ObservableProperty] private bool _noFolders;
    public bool CanGoUp => Path.Length > 0;
    /// <summary>The top of a storage holds the library's own data too: a folder inside it is added.</summary>
    public bool CanAdd => Path.Length > 0;
    public string PlaceText => _root.Length == 0 ? "" : $"{_root[(_root.IndexOf(':') + 1)..]}/{Path}".TrimEnd('/');
    public string AddText => CanAdd ? $"Adds the folder {PlaceText} to the library." : "Go into the folder with your photos (click it).";

    [ObservableProperty] private string _name = "";
    private bool _nameTyped;
    private string _proposed = "";

    // ---- read-only ----
    [ObservableProperty, NotifyPropertyChangedFor(nameof(NeedsPassword))] private bool _readOnly;
    public bool NeedsPassword => ReadOnly && !_host.Repo.ReadOnly.HasPassword;
    [ObservableProperty] private string _password = "";
    [ObservableProperty] private string _passwordAgain = "";

    /// <summary>The folder is in the library: the window closes, the sidebar shows it.</summary>
    public event Action<long>? Added;

    public AddCloudFolderViewModel(LibraryHost host)
    {
        _host = host;
        Form = new StorageFormViewModel("");
        foreach (var c in host.Cloud.Clouds())
        {
            var where = c.Remote[(c.Remote.IndexOf(':') + 1)..].Split('/')[0];
            Destinations.Add(new CloudChoice(where.Length > 0 && c.Kind == "s3" ? $"{c.Name}, bucket {where}" : c.Name, c));
        }
        HasClouds = Destinations.Count > 0;
        Destinations.Add(new CloudChoice("Another storage (fill it in below)", null));
        Destination = Destinations[0];
    }

    protected override void Reload() { }

    partial void OnDestinationChanged(CloudChoice? value)
    {
        Connected = false;
        Folders.Clear();
        if (value?.Cloud is { } cloud) _ = BrowseAsync(cloud);
    }

    partial void OnNameChanged(string value)
    {
        if (value != _proposed) _nameTyped = true;
    }

    partial void OnPathChanged(string value)
    {
        if (_nameTyped || _cloud == null) return;
        _proposed = value.Length == 0 ? "" : $"{value[(value.LastIndexOf('/') + 1)..]} ({_cloud.Name})";
        Name = _proposed;
    }

    /// <summary>Connects the storage filled in below, then shows its folders.</summary>
    [RelayCommand]
    private Task Connect()
    {
        if (Form.Missing() is { } missing)
        {
            Status = missing;
            return Task.CompletedTask;
        }
        return RunAsync(Form.ConnectingText, async ct =>
        {
            var cloud = await Form.ConnectAsync(_host.Cloud, ct);
            var choice = new CloudChoice(cloud.Name, cloud);
            Destinations.Insert(Destinations.Count - 1, choice);
            Destination = choice;
        });
    }

    private Task BrowseAsync(CloudInfo cloud) => RunAsync("Looking into the storage...", async ct =>
    {
        _cloud = cloud;
        _root = await _host.Cloud.StorageRootAsync(cloud, ct);
        Path = "";
        await ListAsync(ct);
        Connected = true;
        Status = "";
    });

    private async Task ListAsync(CancellationToken ct)
    {
        var folders = await _host.Cloud.FoldersAsync(CloudService.Join(_root, Path), ct);
        Folders.Clear();
        foreach (var f in folders) Folders.Add(f);
        NoFolders = folders.Count == 0;
        OnPropertyChanged(nameof(PlaceText));
        OnPropertyChanged(nameof(AddText));
    }

    /// <summary>Into a folder.</summary>
    public Task OpenAsync(string folder) => RunAsync("Looking into the folder...", async ct =>
    {
        Path = Path.Length == 0 ? folder : $"{Path}/{folder}";
        await ListAsync(ct);
        Status = "";
    });

    [RelayCommand]
    private Task Up() => !CanGoUp ? Task.CompletedTask : RunAsync("Looking into the folder...", async ct =>
    {
        Path = Path.Contains('/') ? Path[..Path.LastIndexOf('/')] : "";
        await ListAsync(ct);
        Status = "";
    });

    [RelayCommand]
    private void Add()
    {
        if (_cloud is not { } cloud || !CanAdd) return;
        var remote = CloudService.Join(_root, Path);
        if (_host.Repo.ListSources().FirstOrDefault(s => s.Remote == remote) is { } there)
        {
            Status = $"This folder is in the library already: {there.Name}.";
            return;
        }
        if (string.IsNullOrWhiteSpace(Name))
        {
            Status = "Give it a name.";
            return;
        }
        var locks = _host.Repo.ReadOnly;
        if (NeedsPassword)
        {
            if (Password.Length == 0)
            {
                Status = "Choose the password for read-only folders.";
                return;
            }
            if (Password != PasswordAgain)
            {
                Status = "The two passwords are not the same.";
                return;
            }
        }
        var source = _host.Cloud.AddCloudSourceAt(cloud.Id, remote, Name.Trim());
        if (ReadOnly)
        {
            if (!locks.HasPassword) locks.SetPassword(Password);
            locks.Protect(source.Id);
        }
        Password = PasswordAgain = "";
        _host.Indexer.RequestScan(source.Id);
        Added?.Invoke(source.Id);
    }
}
