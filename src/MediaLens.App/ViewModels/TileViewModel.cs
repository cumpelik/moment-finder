using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using MediaLens.App.Services;
using MediaLens.Library;

namespace MediaLens.App.ViewModels;

/// <summary>One thumbnail of the grid. The bitmap exists only while the tile is on screen.</summary>
public sealed partial class TileViewModel : ObservableObject
{
    private CancellationTokenSource? _loading;

    public MediaItem Item { get; }
    /// <summary>Video: where the search matched, playback starts there.</summary>
    public int StartMs { get; }

    [ObservableProperty] private Bitmap? _thumb;
    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private bool _inBucket;

    public TileViewModel(MediaItem item, int startMs)
    {
        Item = item;
        StartMs = startMs;
    }

    public bool IsVideo => Item.IsVideo;

    public string? DurationText => Item.DurationMs is { } ms ? Format.Duration(ms) : null;

    public string ToolTip => $"{Item.FileName}\n{Item.Taken:d. M. yyyy H:mm}" + (Item.Camera is { } c ? $"\n{c}" : "");

    /// <summary>How many tiles on screen show this item (grouped by person, one item can be in several groups).</summary>
    private int _shown;

    public void Realize(ThumbnailLoader loader)
    {
        _shown++;
        if (Thumb != null || _loading != null) return;
        var cts = _loading = new CancellationTokenSource();
        _ = LoadAsync(loader, cts);
    }

    private async Task LoadAsync(ThumbnailLoader loader, CancellationTokenSource cts)
    {
        try
        {
            var bitmap = await loader.LoadAsync(Item, StartMs, cts.Token);
            if (!cts.IsCancellationRequested) Thumb = bitmap;
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            if (_loading == cts) _loading = null;
            cts.Dispose();
        }
    }

    /// <summary>Scrolled away: stop loading and let the bitmap go (the GC frees it once no frame uses it).</summary>
    public void Unrealize()
    {
        if (--_shown > 0) return;
        _shown = 0;
        _loading?.Cancel();
        _loading = null;
        Thumb = null;
    }
}
