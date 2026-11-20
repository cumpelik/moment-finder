using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using MediaLens.App.ViewModels;

namespace MediaLens.App.Views;

public partial class MainWindow : Window
{
    private MainViewModel? _vm;

    private UniformGridLayout TileLayout => (UniformGridLayout)Repeater.Layout!;

    public MainWindow()
    {
        InitializeComponent();
        Repeater.ElementPrepared += (_, e) => (e.Element.DataContext as TileViewModel)?.Realize(_vm!.Thumbnails);
        Repeater.ElementClearing += (_, e) => (e.Element.DataContext as TileViewModel)?.Unrealize();
        Repeater.Tapped += OnTileTapped;
        Repeater.DoubleTapped += OnTileDoubleTapped;
        AddHandler(KeyDownEvent, OnWindowKeyDown, RoutingStrategies.Tunnel);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm != null)
        {
            _vm.PlayerRequested -= OpenPlayer;
            _vm.PropertyChanged -= OnVmPropertyChanged;
            _vm.ResultsReset -= OnResultsReset;
        }
        _vm = DataContext as MainViewModel;
        if (_vm == null) return;
        _vm.PlayerRequested += OpenPlayer;
        _vm.PropertyChanged += OnVmPropertyChanged;
        _vm.ResultsReset += OnResultsReset;
        ApplyTileSize();
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.TileSize)) ApplyTileSize();
    }

    private void OnResultsReset() => GridScroll.Offset = default;

    private void ApplyTileSize()
    {
        if (_vm == null) return;
        TileLayout.MinItemWidth = _vm.TileSize;
        TileLayout.MinItemHeight = _vm.TileSize;
    }

    // ---- grid ----

    private static TileViewModel? TileOf(RoutedEventArgs e) =>
        (e.Source as Visual)?.GetSelfAndVisualAncestors().OfType<Border>()
            .FirstOrDefault(b => b.Classes.Contains("tile"))?.DataContext as TileViewModel;

    private void OnTileTapped(object? sender, TappedEventArgs e)
    {
        if (_vm == null || TileOf(e) is not { } tile) return;
        _vm.Select(tile,
            toggle: e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta),
            range: e.KeyModifiers.HasFlag(KeyModifiers.Shift));
        GridScroll.Focus();
    }

    private void OnTileDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (_vm != null && TileOf(e) is { } tile) _vm.Open(tile);
    }

    /// <summary>Tiles per row, for Up / Down.</summary>
    private int Columns()
    {
        var width = GridScroll.Viewport.Width;
        var tile = TileLayout.MinItemWidth + TileLayout.MinColumnSpacing;
        return Math.Max(1, (int)((width + TileLayout.MinColumnSpacing) / tile));
    }

    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (_vm == null) return;
        // Typing in the search box (or another text field) is not a shortcut.
        if (e.Source is TextBox || e.Source is NumericUpDown || e.Source is AutoCompleteBox) return;
        var ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta);
        if (_vm.IsReviewMode)
        {
            switch (e.Key)
            {
                case Key.Y: _vm.People.ReviewYesCommand.Execute(null); break;
                case Key.N: _vm.People.ReviewNoCommand.Execute(null); break;
                case Key.S or Key.Right: _vm.People.ReviewSkipCommand.Execute(null); break;
                case Key.O: _ = ReviewOtherAsync(); break;
                default: return;
            }
            e.Handled = true;
            return;
        }
        if (_vm.ShowPersonFaces && e.Key == Key.Delete)
        {
            _vm.People.RejectSelectedCommand.Execute(null);
            e.Handled = true;
            return;
        }
        var shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        TileViewModel? moved = null;
        switch (e.Key)
        {
            case Key.Left when ctrl && _vm.IsBucketScope:
                _vm.MoveSelectionInBucket(-1);
                break;
            case Key.Right when ctrl && _vm.IsBucketScope:
                _vm.MoveSelectionInBucket(1);
                break;
            case Key.Left: moved = _vm.MoveSelection(-1, shift); break;
            case Key.Right: moved = _vm.MoveSelection(1, shift); break;
            case Key.Up: moved = _vm.MoveSelection(-Columns(), shift); break;
            case Key.Down: moved = _vm.MoveSelection(Columns(), shift); break;
            case Key.Home: moved = _vm.MoveSelection(-_vm.Tiles.Count, shift); break;
            case Key.End: moved = _vm.MoveSelection(_vm.Tiles.Count, shift); break;
            case Key.Enter when _vm.AnchorTile is { } tile:
                _vm.Open(tile);
                break;
            case Key.A when ctrl:
                _vm.SelectAll();
                break;
            case Key.F when ctrl:
                SearchBox.Focus();
                SearchBox.SelectAll();
                break;
            case Key.B:
                _vm.AddSelectionToBucketCommand.Execute(null);
                break;
            case Key.Delete when _vm.IsBucketScope:
                _vm.RemoveSelectionFromBucketCommand.Execute(null);
                break;
            case Key.P:
                _vm.PlayAllCommand.Execute(null);
                break;
            case Key.S:
                _vm.ShuffleCommand.Execute(null);
                break;
            default:
                return;
        }
        e.Handled = true;
        if (moved != null) BringIntoView(moved);
    }

    private void BringIntoView(TileViewModel tile)
    {
        var index = _vm!.Tiles.IndexOf(tile);
        if (index >= 0) Repeater.GetOrCreateElement(index).BringIntoView();
    }

    private void OnSearchKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            _vm?.SearchNowCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            if (!string.IsNullOrEmpty(SearchBox.Text)) SearchBox.Text = "";
            else GridScroll.Focus();
            e.Handled = true;
        }
        else if (e.Key == Key.Down)
        {
            GridScroll.Focus();
            if (_vm?.MoveSelection(0, false) is { } t) BringIntoView(t);
            e.Handled = true;
        }
    }

    private void OnOpenCloud(object? sender, RoutedEventArgs e) => OpenCloud();

    private void OpenCloud()
    {
        if (_vm == null) return;
        new CloudWindow { DataContext = _vm.CreateCloudViewModel() }.Show(this);
    }

    // ---- player ----

    private void OpenPlayer(PlayerViewModel player)
    {
        var window = new PlayerWindow { DataContext = player };
        window.Closed += (_, _) =>
        {
            // Back in the grid on the item the viewer ended on.
            var id = player.Current.Item.Id;
            player.Dispose();
            if (_vm?.Tiles.FirstOrDefault(t => t.Item.Id == id) is { } tile)
            {
                _vm.Select(tile, false, false);
                BringIntoView(tile);
            }
            Activate();
            GridScroll.Focus();
        };
        window.Show(this);
    }

    // ---- sidebar ----

    private async void OnHeaderAdd(object? sender, RoutedEventArgs e)
    {
        if (_vm == null || (sender as Control)?.DataContext is not SidebarHeader header) return;
        if (header.Section == SidebarKind.Source)
        {
            var menu = new ContextMenu();
            var folder = new MenuItem { Header = "Folder..." };
            folder.Click += async (_, _) => await AddSourceAsync();
            menu.Items.Add(folder);
            if (MainViewModel.ApplePhotosOriginals() != null)
            {
                var photos = new MenuItem { Header = "Apple Photos library" };
                photos.Click += (_, _) => _vm.AddApplePhotos();
                menu.Items.Add(photos);
            }
            var cloud = new MenuItem { Header = "Folder in a cloud..." };
            cloud.Click += (_, _) => OpenCloud();
            menu.Items.Add(cloud);
            menu.Open(sender as Control);
        }
        else
        {
            var name = await Dialogs.PromptAsync(this, "New bucket", "Name (e.g. Photo book 2025)", "Photo book");
            if (!string.IsNullOrWhiteSpace(name)) _vm.CreateBucket(name);
        }
    }

    private async void OnAddSource(object? sender, RoutedEventArgs e) => await AddSourceAsync();

    private async Task AddSourceAsync()
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Folder with photos and videos",
            AllowMultiple = true,
        });
        foreach (var folder in folders)
            if (folder.TryGetLocalPath() is { } path)
                _vm?.AddSource(path);
    }

    private static SidebarEntry? EntryOf(object? sender) => (sender as Control)?.DataContext as SidebarEntry;

    private async void OnRenameEntry(object? sender, RoutedEventArgs e)
    {
        if (_vm == null || EntryOf(sender) is not { } entry) return;
        if (entry.Kind == SidebarKind.Bucket)
        {
            var name = await Dialogs.PromptAsync(this, "Rename bucket", "Name", entry.Name);
            if (!string.IsNullOrWhiteSpace(name)) _vm.RenameBucket(entry, name);
        }
        else if (entry.Kind == SidebarKind.Person)
        {
            var name = await Dialogs.PromptNameAsync(this, "Rename person", _vm.People.PersonNames(), entry.Name);
            if (name != null) _vm.RenamePerson(entry, name);
        }
    }

    // ---- people ----

    private async void OnNameCluster(object? sender, RoutedEventArgs e)
    {
        if (_vm == null || (sender as Control)?.DataContext is not CropViewModel card) return;
        var name = await Dialogs.PromptNameAsync(this, "Who is this?", _vm.People.PersonNames());
        if (name == null) return;
        if (card.FigureKey != null) _vm.People.NameFigure(card, name);
        else _vm.People.NameCluster(card, name);
    }

    private void OnIgnoreCluster(object? sender, RoutedEventArgs e)
    {
        if (_vm == null || (sender as Control)?.DataContext is not CropViewModel card) return;
        if (card.FigureKey != null) _vm.People.IgnoreFigure(card);
        else _vm.People.IgnoreCluster(card);
    }

    /// <summary>A figure in its photo or video, with the boxes on, so it is clear who it is.</summary>
    private void OnShowFigure(object? sender, RoutedEventArgs e)
    {
        if (_vm != null && (sender as Control)?.DataContext is CropViewModel card) _vm.OpenMedia(card.MediaId, card.TimeMs, showPeople: true);
    }

    private async void OnReviewOther(object? sender, RoutedEventArgs e) => await ReviewOtherAsync();

    private async Task ReviewOtherAsync()
    {
        if (_vm == null) return;
        var name = await Dialogs.PromptNameAsync(this, "Who is it?", _vm.People.PersonNames());
        if (name != null) _vm.People.ReviewOther(name);
    }

    private void OnCropTapped(object? sender, TappedEventArgs e)
    {
        if ((sender as Control)?.DataContext is CropViewModel crop) crop.IsSelected = !crop.IsSelected;
    }

    /// <summary>Double click on a face: the photo it comes from.</summary>
    private void OnCropDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (_vm != null && (sender as Control)?.DataContext is CropViewModel crop) _vm.OpenMedia(crop.MediaId);
    }

    private void OnRescanEntry(object? sender, RoutedEventArgs e) => _vm?.RescanCommand.Execute(null);

    private async void OnRemoveEntry(object? sender, RoutedEventArgs e)
    {
        if (_vm == null || EntryOf(sender) is not { } entry) return;
        switch (entry.Kind)
        {
            case SidebarKind.Source when await Dialogs.ConfirmAsync(this, "Remove source",
                $"Remove \"{entry.Name}\" from the library? The files on the disk stay untouched; thumbnails, analysis and bucket entries of the folder are dropped."):
                _vm.RemoveSource(entry);
                break;
            case SidebarKind.Bucket when await Dialogs.ConfirmAsync(this, "Delete bucket",
                $"Delete the bucket \"{entry.Name}\"? Only the bucket goes away, the photos and videos stay."):
                _vm.DeleteBucket(entry);
                break;
            case SidebarKind.Person when await Dialogs.ConfirmAsync(this, "Forget person",
                $"Forget \"{entry.Name}\"? Their faces become unnamed again; the photos stay."):
                _vm.DeletePerson(entry);
                break;
        }
    }

    private async void OnExportBucket(object? sender, RoutedEventArgs e)
    {
        if (_vm == null) return;
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Copy the bucket to" });
        if (folders.Count == 0 || folders[0].TryGetLocalPath() is not { } target) return;
        var progress = new Progress<(int Done, int Total)>(p => _vm.ResultSummary = $"Copying {p.Done} / {p.Total}...");
        var result = await _vm.ExportBucketAsync(target, progress);
        if (result == null) return;
        var message = $"Copied {result.Copied}" + (result.Skipped > 0 ? $", {result.Skipped} were already there" : "") + $" to {target}.";
        if (result.Errors.Count > 0)
            message += $"\n\n{result.Errors.Count} failed:\n" + string.Join("\n", result.Errors.Take(10));
        await Dialogs.InfoAsync(this, "Bucket copied", message);
        _vm.SearchNowCommand.Execute(null);
    }
}
