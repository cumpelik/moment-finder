using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using MomentFinder.App.Services;
using MomentFinder.Library;

namespace MomentFinder.App.ViewModels;

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

    /// <summary>A 360 photo or video (Insta360 original): can be looked around in.</summary>
    public bool Is360 => Media.Fisheye360.IsDualFisheye(Item.RelPath);

    /// <summary>A 360 video's playing copy: ready (green badge), being made (orange), or not yet.</summary>
    [ObservableProperty] private bool _ready360;
    [ObservableProperty] private bool _making360;
    [ObservableProperty] private string _tip360 = "360: drag to look around";

    private void Refresh360()
    {
        if (!Is360) return;
        _ = Task.Run(() =>
        {
            // A photo needs no copy: it can be looked around in right away.
            bool? state = IsVideo ? Media.Proxy360.State(Item) : true;
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                Ready360 = state == true;
                Making360 = state == false;
                Tip360 = state switch
                {
                    true => "360, ready: plays right away, drag to look around",
                    false => "360, being prepared for smooth playing right now",
                    _ => "360, not prepared yet: opening it prepares it first (it takes a few minutes)",
                };
            });
        });
    }

    public string? DurationText => Item.DurationMs is { } ms ? Format.Duration(ms) : null;

    public string ToolTip => $"{Item.FileName}\n{Item.Taken:d. M. yyyy H:mm}" + (Item.Camera is { } c ? $"\n{c}" : "") + (Note is { } n ? $"\n\n{n}" : "");

    /// <summary>Why the tile is in the list (the best moments of a video: when, who, how much).</summary>
    public string? Note { get; init; }

    /// <summary>How many tiles on screen show this item (grouped by person, one item can be in several groups).</summary>
    private int _shown;

    public void Realize(ThumbnailLoader loader)
    {
        if (_shown++ == 0 && Is360)
        {
            Media.Proxy360.Changed += Refresh360;
            Refresh360();
        }
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
        Media.Proxy360.Changed -= Refresh360;
        _loading?.Cancel();
        _loading = null;
        Thumb = null;
    }
}
