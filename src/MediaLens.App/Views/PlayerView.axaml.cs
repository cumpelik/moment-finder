using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using MediaLens.App.ViewModels;

namespace MediaLens.App.Views;

/// <summary>
/// The picture or video with its controls: full screen in the PlayerWindow, or docked in a panel under the
/// thumbnails. Full screen hides the controls until the mouse moves; docked they stay.
/// </summary>
public partial class PlayerView : UserControl
{
    private readonly DispatcherTimer _hideOverlay;
    private PlayerViewModel? _vm;
    private Point _lastPointer;
    private bool _docked;

    /// <summary>The full screen button (or F): the window toggles full screen, the panel opens a window.</summary>
    public event Action? FullScreenRequested;
    /// <summary>A dialog closed: the host gives the keyboard back to the viewer.</summary>
    public event Action? KeyboardWanted;

    public PlayerView()
    {
        InitializeComponent();
        _hideOverlay = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.5) };
        _hideOverlay.Tick += (_, _) =>
        {
            if (Overlay.IsPointerOver) return;
            _hideOverlay.Stop();
            Overlay.Opacity = 0;
            Root.Cursor = new Cursor(StandardCursorType.None);
        };
        Root.PointerMoved += OnPointerMoved;
        Root.PointerWheelChanged += (_, e) =>
        {
            if (e.Delta.Y > 0) _vm?.Previous();
            else if (e.Delta.Y < 0) _vm?.Next();
        };
        Root.DoubleTapped += (_, _) => FullScreenRequested?.Invoke();
        FullScreenButton.Click += (_, _) => FullScreenRequested?.Invoke();
        SeekBar.AddHandler(PointerPressedEvent, (_, _) => _vm?.BeginSeek(), RoutingStrategies.Tunnel, handledEventsToo: true);
        SeekBar.AddHandler(PointerReleasedEvent, (_, _) => _vm?.SeekTo(SeekBar.Value), RoutingStrategies.Tunnel, handledEventsToo: true);
        Root.Focusable = true;
    }

    /// <summary>In a panel: the controls always show and there is no close button.</summary>
    public bool Docked
    {
        get => _docked;
        set
        {
            _docked = value;
            CloseButton.IsVisible = !value;
            ShowOverlay();
        }
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm != null)
        {
            _vm.VideoFrameChanged -= OnVideoFrame;
            _vm.PeopleChanged -= DrawPeople;
        }
        _vm = DataContext as PlayerViewModel;
        VideoImage.Source = null;
        PeopleLayer.Children.Clear();
        if (_vm == null) return;
        _vm.VideoFrameChanged += OnVideoFrame;
        _vm.PeopleChanged += DrawPeople;
        ShowOverlay();
    }

    public void FocusViewer() => Root.Focus();

    private void OnVideoFrame()
    {
        if (!ReferenceEquals(VideoImage.Source, _vm?.VideoFrame))
        {
            VideoImage.Source = _vm?.VideoFrame;
            if (_vm?.VideoFrame is { } frame)
            {
                _vm.ContentWidth = frame.PixelSize.Width;
                _vm.ContentHeight = frame.PixelSize.Height;
                DrawPeople();
            }
        }
        VideoImage.InvalidateVisual();
    }

    /// <summary>Boxes around faces and people; a click says who it is (the seed for someone in a helmet).</summary>
    private void DrawPeople()
    {
        PeopleLayer.Children.Clear();
        if (_vm == null) return;
        var scale = Math.Max(1, _vm.ContentWidth / 1280);
        foreach (var box in _vm.PeopleBoxes())
        {
            var color = box.Known ? Avalonia.Media.Brushes.LimeGreen : Avalonia.Media.Brushes.Orange;
            var label = new Border
            {
                Background = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromArgb(200, 0, 0, 0)),
                Padding = new Thickness(4 * scale, 1 * scale),
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top,
                Child = new TextBlock { Text = box.Label, Foreground = Avalonia.Media.Brushes.White, FontSize = 13 * scale },
            };
            var frame = new Border
            {
                Width = box.W,
                Height = box.H,
                BorderBrush = color,
                BorderThickness = new Thickness(box.IsFace ? 2 * scale : 3 * scale),
                Background = Avalonia.Media.Brushes.Transparent,
                Cursor = new Cursor(StandardCursorType.Hand),
                Child = label,
            };
            ToolTip.SetTip(frame, "Click: who is it?");
            var id = box.DetectionId;
            var vm = _vm;
            frame.PointerPressed += async (_, e) =>
            {
                e.Handled = true;
                if (TopLevel.GetTopLevel(this) is not Window owner) return;
                var name = await Dialogs.PromptNameAsync(owner, "Who is it?", vm.Names());
                if (name != null) vm.NameDetection(id, name);
                KeyboardWanted?.Invoke();
            };
            Canvas.SetLeft(frame, box.X);
            Canvas.SetTop(frame, box.Y);
            PeopleLayer.Children.Add(frame);
        }
    }

    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        var p = e.GetPosition(this);
        // Some platforms send a move without a move (e.g. when the image changes).
        if (Math.Abs(p.X - _lastPointer.X) + Math.Abs(p.Y - _lastPointer.Y) < 3) return;
        _lastPointer = p;
        ShowOverlay();
    }

    public void ShowOverlay()
    {
        Overlay.Opacity = 1;
        Root.Cursor = Cursor.Default;
        _hideOverlay.Stop();
        if (!Docked) _hideOverlay.Start();
    }

    /// <summary>The keys both hosts share; Esc and F belong to the host.</summary>
    public bool HandleKey(KeyEventArgs e)
    {
        if (_vm == null) return false;
        var shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        switch (e.Key)
        {
            case Key.Left when shift: _vm.SeekBy(-10); break;
            case Key.Right when shift: _vm.SeekBy(10); break;
            case Key.Left or Key.PageUp: _vm.Previous(); break;
            case Key.Right or Key.PageDown: _vm.Next(); break;
            case Key.Space: _vm.TogglePlay(); ShowOverlay(); break;
            case Key.B: _vm.AddToBucket(); ShowOverlay(); break;
            case Key.I: _vm.ShowPeople = !_vm.ShowPeople; break;
            case Key.Up: _vm.ChangeVolume(10); break;
            case Key.Down: _vm.ChangeVolume(-10); break;
            default: return false;
        }
        return true;
    }
}
