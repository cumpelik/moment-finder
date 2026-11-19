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
        if (e.Source is TextBox || e.Source is NumericUpDown) return;
        var ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta);
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
            await AddSourceAsync();
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
        if (_vm == null || EntryOf(sender) is not { Kind: SidebarKind.Bucket } entry) return;
        var name = await Dialogs.PromptAsync(this, "Rename bucket", "Name", entry.Name);
        if (!string.IsNullOrWhiteSpace(name)) _vm.RenameBucket(entry, name);
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
