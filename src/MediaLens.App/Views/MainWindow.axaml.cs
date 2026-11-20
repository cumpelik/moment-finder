using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
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
        Repeater.ElementPrepared += OnTilePrepared;
        Repeater.ElementClearing += OnTileClearing;
        Repeater.Tapped += OnTileTapped;
        Repeater.DoubleTapped += OnTileDoubleTapped;
        GroupRepeater.Tapped += OnTileTapped;
        GroupRepeater.DoubleTapped += OnTileDoubleTapped;
        Preview.FullScreenRequested += OnPanelFullScreen;
        Preview.KeyboardWanted += () => GridScroll.Focus();
        PreviewSplitter.DragCompleted += OnPreviewSplitterDragged;
        SidebarSplitter.DragCompleted += (_, _) =>
        {
            if (_vm?.SidebarVisible == true) _vm.SaveSize("sidebar_width", MainSplit.ColumnDefinitions[0].ActualWidth);
        };
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
        ApplySidebar();
        // After the first layout, so one row of thumbnails can be measured.
        Avalonia.Threading.Dispatcher.UIThread.Post(ApplyPreviewPanel, DispatcherPriority.Loaded);
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.TileSize)) ApplyTileSize();
        if (e.PropertyName is nameof(MainViewModel.ShowPreviewPanel) or nameof(MainViewModel.PreviewMode)) ApplyPreviewPanel();
        if (e.PropertyName == nameof(MainViewModel.SidebarVisible)) ApplySidebar();
    }

    private void OnResultsReset()
    {
        GridScroll.Offset = default;
        // Other results: the panel's item may not be among them.
        ClosePanelPlayer();
    }

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
        var toggle = e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta);
        var range = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        _vm.Select(tile, toggle, range);
        if (!toggle && !range) ShowInPanel(tile);
        GridScroll.Focus();
    }

    private void OnTilePlay(object? sender, RoutedEventArgs e)
    {
        if (_vm != null && (sender as Control)?.DataContext is TileViewModel tile) _vm.Open(tile);
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
            // With the panel: space plays / pauses it, I shows who is who in it.
            case Key.Space when _panelPlayer != null:
                _panelPlayer.TogglePlay();
                break;
            case Key.I when _panelPlayer != null:
                _panelPlayer.ShowPeople = !_panelPlayer.ShowPeople;
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
        if (moved != null)
        {
            BringIntoView(moved);
            if (!shift) ShowInPanel(moved);
        }
    }

    private void BringIntoView(TileViewModel tile)
    {
        if (!_vm!.IsGrouped)
        {
            var index = _vm.Tiles.IndexOf(tile);
            if (index >= 0) Repeater.GetOrCreateElement(index).BringIntoView();
            return;
        }
        var group = _vm.Groups.Select((g, i) => (g, i)).FirstOrDefault(x => x.g.Tiles.Contains(tile));
        if (group.g == null) return;
        var container = GroupRepeater.GetOrCreateElement(group.i);
        container.UpdateLayout();
        if (container.GetVisualDescendants().OfType<ItemsRepeater>().FirstOrDefault() is { } inner)
            inner.GetOrCreateElement(group.g.Tiles.ToList().IndexOf(tile)).BringIntoView();
        else container.BringIntoView();
    }

    /// <summary>A tile came on screen (plain grid or a group): load its thumbnail.</summary>
    private void OnTilePrepared(object? sender, ItemsRepeaterElementPreparedEventArgs e) =>
        (e.Element.DataContext as TileViewModel)?.Realize(_vm!.Thumbnails);

    private void OnTileClearing(object? sender, ItemsRepeaterElementClearingEventArgs e) =>
        (e.Element.DataContext as TileViewModel)?.Unrealize();

    private void OnDateFlyoutOpening(object? sender, EventArgs e) => _vm?.LoadDateChoices();

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

    private PlayerViewModel? _panelPlayer;

    protected override void OnClosed(EventArgs e)
    {
        ClosePanelPlayer();
        base.OnClosed(e);
    }

    private void OpenPlayer(PlayerViewModel player, bool inWindow)
    {
        if (!inWindow && _vm is { ShowPreviewPanel: true })
        {
            SetPanelPlayer(player);
            return;
        }
        // Full screen over the panel: the panel stops meanwhile.
        _panelPlayer?.Pause();
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

    /// <summary>A click or an arrow in the grid: the panel shows that item (it only moves when the results are the same).</summary>
    private void ShowInPanel(TileViewModel tile)
    {
        if (_vm is not { ShowPreviewPanel: true }) return;
        var index = _vm.Tiles.IndexOf(tile);
        if (index < 0) return;
        if (_panelPlayer != null && _panelPlayer.Plays(_vm.Tiles)) _panelPlayer.GoTo(index);
        else SetPanelPlayer(_vm.CreatePreview(index));
    }

    private void SetPanelPlayer(PlayerViewModel player)
    {
        ClosePanelPlayer();
        _panelPlayer = player;
        player.PropertyChanged += OnPanelPlayerChanged;
        player.CloseRequested += ClosePanelPlayer;
        Preview.DataContext = player;
        Preview.Docked = true;
        Preview.IsVisible = true;
        player.Start();
    }

    private void ClosePanelPlayer()
    {
        if (_panelPlayer is not { } old) return;
        _panelPlayer = null;
        old.PropertyChanged -= OnPanelPlayerChanged;
        old.CloseRequested -= ClosePanelPlayer;
        Preview.IsVisible = false;
        Preview.DataContext = null;
        old.Dispose();
    }

    /// <summary>Previous / next in the panel (or its slideshow): the grid selects the same item.</summary>
    private void OnPanelPlayerChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(PlayerViewModel.Index) || _vm == null || _panelPlayer == null) return;
        var id = _panelPlayer.Current.Item.Id;
        if (_vm.Tiles.FirstOrDefault(t => t.Item.Id == id) is not { } tile || tile.IsSelected && _vm.SelectedTiles().Count() == 1) return;
        _vm.Select(tile, false, false);
        BringIntoView(tile);
    }

    /// <summary>
    /// Below: one row of thumbnails, the viewer takes the rest. Right: one column of thumbnails. The splitter
    /// changes it and the size the user dragged is kept.
    /// </summary>
    private void ApplyPreviewPanel()
    {
        if (_vm == null) return;
        var mode = _vm.ShowPreviewPanel ? _vm.PreviewMode : PreviewMode.Window;
        var rows = BrowseArea.RowDefinitions;
        var cols = BrowseArea.ColumnDefinitions;
        var star = new GridLength(1, GridUnitType.Star);
        var none = new GridLength(0);
        switch (mode)
        {
            case PreviewMode.Below:
                rows[0].Height = new GridLength(_vm.SavedSize("preview_below_size") ?? OneRowHeight());
                rows[2].Height = star;
                cols[0].Width = star;
                cols[2].Width = none;
                Place(PreviewSplitter, 1, 0);
                Place(PreviewHost, 2, 0);
                PreviewSplitter.ResizeDirection = GridResizeDirection.Rows;
                (PreviewSplitter.Width, PreviewSplitter.Height) = (double.NaN, 6);
                break;
            case PreviewMode.Right:
                cols[0].Width = new GridLength(_vm.SavedSize("preview_right_size") ?? OneColumnWidth());
                cols[2].Width = star;
                rows[0].Height = star;
                rows[2].Height = none;
                Place(PreviewSplitter, 0, 1);
                Place(PreviewHost, 0, 2);
                PreviewSplitter.ResizeDirection = GridResizeDirection.Columns;
                (PreviewSplitter.Width, PreviewSplitter.Height) = (6, double.NaN);
                break;
            default:
                rows[0].Height = star;
                rows[2].Height = none;
                cols[0].Width = star;
                cols[2].Width = none;
                ClosePanelPlayer();
                return;
        }
        if (_vm.AnchorTile is { } tile)
        {
            ShowInPanel(tile);
            Avalonia.Threading.Dispatcher.UIThread.Post(() => BringIntoView(tile), DispatcherPriority.Background);
        }
    }

    private static void Place(Control c, int row, int column)
    {
        Grid.SetRow(c, row);
        Grid.SetColumn(c, column);
    }

    /// <summary>The height of one row of thumbnails as laid out now (with a group title when grouped).</summary>
    private double OneRowHeight()
    {
        var tile = Repeater.GetVisualDescendants().OfType<Border>().FirstOrDefault(b => b.Classes.Contains("tile"))?.Bounds.Height
                   ?? GroupRepeater.GetVisualDescendants().OfType<Border>().FirstOrDefault(b => b.Classes.Contains("tile"))?.Bounds.Height;
        var height = (tile is > 0 ? tile.Value : _vm!.TileSize) + GridScroll.Padding.Top + GridScroll.Padding.Bottom + 4;
        return _vm!.IsGrouped ? height + 44 : height;
    }

    private double OneColumnWidth() => _vm!.TileSize + GridScroll.Padding.Left + GridScroll.Padding.Right + 16;

    private void OnPreviewSplitterDragged(object? sender, VectorEventArgs e)
    {
        if (_vm == null) return;
        if (_vm.PreviewMode == PreviewMode.Below) _vm.SaveSize("preview_below_size", BrowseArea.RowDefinitions[0].ActualHeight);
        if (_vm.PreviewMode == PreviewMode.Right) _vm.SaveSize("preview_right_size", BrowseArea.ColumnDefinitions[0].ActualWidth);
    }

    private void ApplySidebar()
    {
        if (_vm == null) return;
        MainSplit.ColumnDefinitions[0].Width = _vm.SidebarVisible ? new GridLength(_vm.SavedSize("sidebar_width") ?? 250) : new GridLength(0);
    }

    /// <summary>The panel's full screen button: the same item in the full screen viewer.</summary>
    private void OnPanelFullScreen()
    {
        if (_vm == null || _panelPlayer == null) return;
        var id = _panelPlayer.Current.Item.Id;
        if (_vm.Tiles.FirstOrDefault(t => t.Item.Id == id) is { } tile) _vm.Open(tile);
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
