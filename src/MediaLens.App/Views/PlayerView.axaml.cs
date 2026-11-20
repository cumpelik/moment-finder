using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
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
            // A 360 picture zooms; otherwise the wheel goes through the items.
            if (_vm is { Is360: true } vm) vm.Look(0, 0, e.Delta.Y > 0 ? 0.9 : 1.1);
            else if (e.Delta.Y > 0) _vm?.Previous();
            else if (e.Delta.Y < 0) _vm?.Next();
        };
        // Dragging a 360 picture turns the view (like grabbing the scene).
        // A box is asked about on release, so a drag that starts on a person still turns a 360 view.
        Root.AddHandler(PointerPressedEvent, (_, e) =>
        {
            if (!e.GetCurrentPoint(Root).Properties.IsLeftButtonPressed) return;
            // The controls (time line, buttons) handle their own mouse: no turning the view, no box under them.
            if (e.Source is Visual onControls && onControls.GetSelfAndVisualAncestors()
                    .Any(v => v == Overlay || v is Avalonia.Controls.Primitives.TemplatedControl and not UserControl))
            {
                _pressedBox = null;
                _dragFrom = null;
                return;
            }
            _pressAt = e.GetPosition(Root);
            _pressedBox = (e.Source as Visual)?.GetSelfAndVisualAncestors().OfType<Control>().Select(c => c.Tag).OfType<long>().FirstOrDefault();
            if (_vm is not { Is360: true }) return;
            _dragFrom = _pressAt;
            e.Pointer.Capture(Root);
        }, RoutingStrategies.Tunnel);
        Root.AddHandler(PointerReleasedEvent, (_, e) =>
        {
            // Only a capture of our own is let go: a button holds the pointer until its click.
            if (_dragFrom != null)
            {
                _dragFrom = null;
                e.Pointer.Capture(null);
            }
            var moved = e.GetPosition(Root) - _pressAt;
            if (_pressedBox is > 0 and var id && Math.Abs(moved.X) + Math.Abs(moved.Y) < 6) AskWho(id);
            _pressedBox = null;
        }, RoutingStrategies.Tunnel);
        // A double click on the picture; quick clicks on a control (one frame further) are not one.
        Root.DoubleTapped += (_, e) =>
        {
            if (e.Source is Visual v && (v == Overlay || Overlay.IsVisualAncestorOf(v))) return;
            FullScreenRequested?.Invoke();
        };
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
        _boxes.Clear();
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
            }
        }
        VideoImage.InvalidateVisual();
        // The boxes follow the people frame by frame.
        if (_vm?.ShowPeople == true) DrawPeople();
    }

    private static readonly Avalonia.Media.Color KnownColor = Avalonia.Media.Color.FromRgb(0x34, 0xD3, 0x99);
    private static readonly Avalonia.Media.Color UnknownColor = Avalonia.Media.Color.FromRgb(0xF5, 0x9E, 0x0B);

    /// <summary>
    /// A box around each person with the name above it (inside only at the top edge of the picture), so a long
    /// name never gets cut by a narrow box. A click on the box or the name says who it is.
    /// </summary>
    /// <summary>The drawn box and name of each person, moved on every frame instead of being made again.</summary>
    private readonly Dictionary<long, (Border Frame, Border Label, TextBlock Text)> _boxes = [];

    private void DrawPeople()
    {
        if (_vm == null)
        {
            PeopleLayer.Children.Clear();
            _boxes.Clear();
            return;
        }
        // Boxes are drawn in the picture's pixels and scaled with it: one screen pixel is this many of them,
        // so lines and names keep the same size on screen for any video and window.
        var shown = Math.Min(Root.Bounds.Width / _vm.ContentWidth, Root.Bounds.Height / _vm.ContentHeight);
        var scale = shown > 0 ? 1 / shown : 1;
        var seen = new HashSet<long>();
        foreach (var box in _vm.PeopleBoxes())
        {
            seen.Add(box.DetectionId);
            if (!_boxes.TryGetValue(box.DetectionId, out var drawn))
            {
                drawn = NewBox(box.DetectionId);
                _boxes[box.DetectionId] = drawn;
                PeopleLayer.Children.Add(drawn.Frame);
                PeopleLayer.Children.Add(drawn.Label);
            }
            var brush = new Avalonia.Media.SolidColorBrush(box.Known ? KnownColor : UnknownColor);
            var (frame, label, text) = drawn;
            frame.Width = box.W;
            frame.Height = box.H;
            frame.CornerRadius = new CornerRadius(6 * scale);
            frame.BorderBrush = brush;
            frame.BorderThickness = new Thickness(2 * scale);
            label.Background = brush;
            label.CornerRadius = new CornerRadius(10 * scale);
            label.Padding = new Thickness(8 * scale, 2 * scale);
            text.Text = box.Label;
            text.FontSize = 13 * scale;
            Canvas.SetLeft(frame, box.X);
            Canvas.SetTop(frame, box.Y);
            Canvas.SetLeft(label, box.X);
            // Above the box; at the top edge of the picture inside it.
            if (box.Y > 28 * scale)
            {
                label.ClearValue(Canvas.TopProperty);
                Canvas.SetBottom(label, _vm.ContentHeight - box.Y + 4 * scale);
            }
            else
            {
                label.ClearValue(Canvas.BottomProperty);
                Canvas.SetTop(label, box.Y + 4 * scale);
            }
        }
        foreach (var gone in _boxes.Keys.Where(k => !seen.Contains(k)).ToList())
        {
            PeopleLayer.Children.Remove(_boxes[gone].Frame);
            PeopleLayer.Children.Remove(_boxes[gone].Label);
            _boxes.Remove(gone);
        }
    }

    private (Border Frame, Border Label, TextBlock Text) NewBox(long detectionId)
    {
        var frame = new Border
        {
            Background = Avalonia.Media.Brushes.Transparent,
            BoxShadow = Avalonia.Media.BoxShadows.Parse("0 0 4 0 #99000000"),
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        var text = new TextBlock { FontWeight = Avalonia.Media.FontWeight.SemiBold, Foreground = Avalonia.Media.Brushes.Black };
        var label = new Border
        {
            BoxShadow = Avalonia.Media.BoxShadows.Parse("0 1 4 0 #80000000"),
            Cursor = new Cursor(StandardCursorType.Hand),
            Child = text,
        };
        ToolTip.SetTip(frame, "Click: who is it?");
        ToolTip.SetTip(label, "Click: who is it?");
        frame.Tag = detectionId;
        label.Tag = detectionId;
        return (frame, label, text);
    }

    private Point _pressAt;
    private long? _pressedBox;

    private async void AskWho(long detectionId)
    {
        if (_vm is not { } vm || TopLevel.GetTopLevel(this) is not Window owner) return;
        var wasRunning = vm.HoldForDialog();
        var box = vm.PeopleBoxes().FirstOrDefault(b => b.DetectionId == detectionId);
        var (action, name) = await Dialogs.AskWhoAsync(owner, vm.Names(), box?.PersonName);
        switch (action)
        {
            case Dialogs.WhoAction.Name: vm.NameDetection(detectionId, name!); break;
            case Dialogs.WhoAction.NotThisPerson when box?.PersonId is { } personId: vm.RejectDetection(detectionId, personId); break;
            case Dialogs.WhoAction.Ignore: vm.IgnoreDetection(detectionId); break;
        }
        vm.ResumeAfterDialog(wasRunning);
        KeyboardWanted?.Invoke();
    }

    private Point? _dragFrom;

    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_dragFrom is { } from && _vm is { Is360: true } vm)
        {
            var at = e.GetPosition(Root);
            var degreesPerPixel = vm.ViewFov / Math.Max(1, Root.Bounds.Width);
            vm.Look(-(at.X - from.X) * degreesPerPixel, (at.Y - from.Y) * degreesPerPixel);
            _dragFrom = at;
        }
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
            case Key.OemOpenBrackets: _vm.StepRate(-1); ShowOverlay(); break;
            case Key.OemCloseBrackets: _vm.StepRate(1); ShowOverlay(); break;
            case Key.L: _vm.ToggleLoop(); ShowOverlay(); break;
            case Key.OemPeriod: _vm.NextFrame(); ShowOverlay(); break;
            case Key.Up: _vm.ChangeVolume(10); break;
            case Key.Down: _vm.ChangeVolume(-10); break;
            default: return false;
        }
        return true;
    }
}
