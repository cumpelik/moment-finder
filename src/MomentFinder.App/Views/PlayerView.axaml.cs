using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MomentFinder.App.ViewModels;

namespace MomentFinder.App.Views;

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
            // The wheel zooms, at the mouse (a 360 picture changes its field of view); it never goes to another item.
            // Not over an open list.
            if (e.Source is not Visual over || !Root.IsVisualAncestorOf(over)) return;
            if (_vm is { Is360: true } vm) vm.Look(0, 0, e.Delta.Y > 0 ? 0.9 : 1.1);
            else if (over != Overlay && !Overlay.IsVisualAncestorOf(over) && e.Delta.Y != 0) ZoomAt(e.GetPosition(Root), e.Delta.Y > 0 ? 1.25 : 0.8);
            e.Handled = true;
        };
        Fit.SizeChanged += (_, _) => SetZoom(_zoom, _pan);
        // Dragging a 360 picture turns the view (like grabbing the scene); dragging a zoomed picture moves it.
        // A box is asked about on release, so a drag that starts on a person still turns a 360 view.
        Root.AddHandler(PointerPressedEvent, (_, e) =>
        {
            if (!e.GetCurrentPoint(Root).Properties.IsLeftButtonPressed) return;
            // The controls (time line, buttons) handle their own mouse: no turning the view, no box under them.
            // An open list or menu (its events come through here too, though it is drawn in its own window) as well:
            // capturing the mouse for turning a 360 view would take the click away from the list.
            if (e.Source is not Visual onControls || !Root.IsVisualAncestorOf(onControls)
                || onControls.GetSelfAndVisualAncestors().TakeWhile(v => v != Root).Any(v => v == Overlay || v is Button or Slider or ComboBox))
            {
                _pressedBox = null;
                _dragFrom = null;
                return;
            }
            _pressAt = e.GetPosition(Root);
            var tags = (e.Source as Visual)?.GetSelfAndVisualAncestors().OfType<Control>().Select(c => c.Tag).ToList() ?? [];
            // The tick of a guessed name sits in the name's label: it says yes, the rest of the box asks who it is.
            _pressedTick = tags.OfType<GuessTick>().FirstOrDefault()?.DetectionId;
            _pressedBox = _pressedTick != null ? null : tags.OfType<long>().FirstOrDefault();
            if (_vm is not { Is360: true } && _zoom == 1) return;
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
            var clicked = Math.Abs(moved.X) + Math.Abs(moved.Y) < 6;
            if (_pressedTick is { } guess && clicked) _vm?.ConfirmGuess(guess);
            else if (_pressedBox is > 0 and var id && clicked) AskWho(id);
            _pressedBox = null;
            _pressedTick = null;
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
        SeekBar.SizeChanged += (_, _) => LayoutLoop();
        SeekBar.TemplateApplied += (_, _) => Dispatcher.UIThread.Post(LayoutLoop, DispatcherPriority.Loaded);
        DragLoopEdge(LoopStartHandle, start: true);
        DragLoopEdge(LoopEndHandle, start: false);
    }

    // ---- the looping part on the time line ----

    /// <summary>Where the time line's thumb stands for a time: left edge and width its center travels (in LoopLayer).</summary>
    private (double Left, double Width)? SeekTrack()
    {
        if (SeekBar.GetVisualDescendants().OfType<Avalonia.Controls.Primitives.Track>().FirstOrDefault() is not { Thumb: { } thumb } track
            || track.TranslatePoint(default, LoopLayer) is not { } origin) return null;
        var width = track.Bounds.Width - thumb.Bounds.Width;
        return width > 0 ? (origin.X + thumb.Bounds.Width / 2, width) : null;
    }

    private void LayoutLoop()
    {
        if (_vm is not { HasLoopPart: true } vm || SeekTrack() is not { } track) return;
        var length = Math.Max(1, vm.VideoLength);
        var from = track.Left + track.Width * Math.Clamp((vm.LoopStartMs ?? 0) / length, 0, 1);
        var to = track.Left + track.Width * Math.Clamp((vm.LoopEndMs ?? length) / length, 0, 1);
        var middle = SeekBar.Bounds.Height / 2;
        LoopBar.Width = Math.Max(2, to - from);
        Canvas.SetLeft(LoopBar, from);
        Canvas.SetTop(LoopBar, middle - LoopBar.Height / 2);
        // The handles stand above the line, beside the part, so the time line's own thumb stays reachable.
        Canvas.SetLeft(LoopStartHandle, from - LoopStartHandle.Width);
        Canvas.SetLeft(LoopEndHandle, to);
        Canvas.SetTop(LoopStartHandle, middle - LoopStartHandle.Height - 2);
        Canvas.SetTop(LoopEndHandle, middle - LoopEndHandle.Height - 2);
        LoopStartHandle.IsVisible = vm.LoopStartMs != null;
        LoopEndHandle.IsVisible = vm.LoopEndMs != null;
        LoopLayer.Opacity = vm.Loop ? 1 : 0.4;
    }

    private void DragLoopEdge(Border handle, bool start)
    {
        handle.PointerPressed += (_, e) =>
        {
            e.Pointer.Capture(handle);
            e.Handled = true;
        };
        handle.PointerMoved += (_, e) =>
        {
            if (!ReferenceEquals(e.Pointer.Captured, handle) || _vm is not { } vm || SeekTrack() is not { } track) return;
            vm.MoveLoopEdge(start, (e.GetPosition(LoopLayer).X - track.Left) / track.Width * vm.VideoLength);
            e.Handled = true;
        };
        handle.PointerReleased += (_, e) =>
        {
            if (!ReferenceEquals(e.Pointer.Captured, handle)) return;
            e.Pointer.Capture(null);
            _vm?.LoopEdgeMoved(start);
            e.Handled = true;
        };
    }

    private void OnVmChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(PlayerViewModel.LoopStartMs) or nameof(PlayerViewModel.LoopEndMs) or nameof(PlayerViewModel.Loop)
            or nameof(PlayerViewModel.VideoLength) or nameof(PlayerViewModel.HasLoopPart))
            LayoutLoop();
        // Another photo or video shows whole again.
        if (e.PropertyName == nameof(PlayerViewModel.Index)) SetZoom(1, default);
    }

    // ---- zooming with the mouse wheel ----

    private const double MaxZoom = 8;
    private double _zoom = 1;
    /// <summary>Where the top left corner of the zoomed view is.</summary>
    private Point _pan;

    /// <summary>Zooms in or out so that the place under the mouse stays under it.</summary>
    private void ZoomAt(Point at, double factor)
    {
        var zoom = Math.Clamp(_zoom * factor, 1, MaxZoom);
        var under = (at - _pan) / _zoom;
        SetZoom(zoom, at - under * zoom);
    }

    private void SetZoom(double zoom, Point pan)
    {
        var room = Fit.Bounds.Size;
        var fit = _vm is { ContentWidth: > 0, ContentHeight: > 0 } ? Math.Min(room.Width / _vm.ContentWidth, room.Height / _vm.ContentHeight) : 0;
        _zoom = fit > 0 ? zoom : 1;
        _pan = _zoom == 1 ? default
            : new Point(Hold(pan.X, _zoom, room.Width, _vm!.ContentWidth * fit), Hold(pan.Y, _zoom, room.Height, _vm.ContentHeight * fit));
        Fit.RenderTransform = _zoom == 1 ? null : new Avalonia.Media.MatrixTransform(new Matrix(_zoom, 0, 0, _zoom, _pan.X, _pan.Y));
        if (_vm?.ShowPeople == true) DrawPeople();
    }

    /// <summary>
    /// One direction of the zoomed picture: while it is smaller than the room it stays in the middle, and once
    /// it is bigger its edges never come inside (no black beside a picture that could fill the place).
    /// </summary>
    private static double Hold(double pan, double zoom, double room, double picture)
    {
        // The black beside the fitted picture, zoomed with it.
        var edge = (room - picture) / 2 * zoom;
        return picture * zoom <= room ? room * (1 - zoom) / 2 : Math.Clamp(pan, room - edge - picture * zoom, -edge);
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
            _vm.PropertyChanged -= OnVmChanged;
        }
        _vm = DataContext as PlayerViewModel;
        SetZoom(1, default);
        VideoImage.Source = null;
        PeopleLayer.Children.Clear();
        _boxes.Clear();
        if (_vm == null) return;
        _vm.VideoFrameChanged += OnVideoFrame;
        _vm.PeopleChanged += DrawPeople;
        _vm.PropertyChanged += OnVmChanged;
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
    private readonly Dictionary<long, (Border Frame, Border Label, TextBlock Text, Border Tick)> _boxes = [];

    /// <summary>The tag of the tick in the label of a guessed name ("David?"): a click on it confirms the name.</summary>
    private sealed record GuessTick(long DetectionId);

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
        var shown = Math.Min(Root.Bounds.Width / _vm.ContentWidth, Root.Bounds.Height / _vm.ContentHeight) * _zoom;
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
            var (frame, label, text, tick) = drawn;
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
            tick.IsVisible = box.Guess;
            if (box.Guess)
            {
                ((TextBlock)tick.Child!).FontSize = 13 * scale;
                tick.CornerRadius = new CornerRadius(8 * scale);
                tick.Padding = new Thickness(5 * scale, 0);
                tick.Margin = new Thickness(6 * scale, 0, -4 * scale, 0);
                ToolTip.SetTip(tick, $"Yes, it is {box.PersonName}");
            }
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
                // A box reaching above the picture (somebody close to a 360 camera) keeps its name in sight.
                Canvas.SetTop(label, Math.Max(box.Y, 0) + 4 * scale);
            }
        }
        foreach (var gone in _boxes.Keys.Where(k => !seen.Contains(k)).ToList())
        {
            PeopleLayer.Children.Remove(_boxes[gone].Frame);
            PeopleLayer.Children.Remove(_boxes[gone].Label);
            _boxes.Remove(gone);
        }
    }

    private (Border Frame, Border Label, TextBlock Text, Border Tick) NewBox(long detectionId)
    {
        var frame = new Border
        {
            Background = Avalonia.Media.Brushes.Transparent,
            BoxShadow = Avalonia.Media.BoxShadows.Parse("0 0 4 0 #99000000"),
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        var text = new TextBlock { FontWeight = Avalonia.Media.FontWeight.SemiBold, Foreground = Avalonia.Media.Brushes.Black };
        var tick = new Border
        {
            Background = Avalonia.Media.Brushes.Black,
            Tag = new GuessTick(detectionId),
            Child = new TextBlock { Text = "\u2713", FontWeight = Avalonia.Media.FontWeight.Bold, Foreground = Avalonia.Media.Brushes.White },
        };
        var label = new Border
        {
            BoxShadow = Avalonia.Media.BoxShadows.Parse("0 1 4 0 #80000000"),
            Cursor = new Cursor(StandardCursorType.Hand),
            Child = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Children = { text, tick } },
        };
        ToolTip.SetTip(frame, "Click: who is it?");
        ToolTip.SetTip(label, "Click: who is it?");
        frame.Tag = detectionId;
        label.Tag = detectionId;
        return (frame, label, text, tick);
    }

    private Point _pressAt;
    private long? _pressedBox;
    private long? _pressedTick;

    private async void AskWho(long detectionId)
    {
        if (_vm is not { } vm || TopLevel.GetTopLevel(this) is not Window owner) return;
        var wasRunning = vm.HoldForDialog();
        var box = vm.PeopleBoxes().FirstOrDefault(b => b.DetectionId == detectionId);
        var (action, name) = await Dialogs.AskWhoAsync(owner, await vm.AskAsync(detectionId), box?.PersonName, box?.Guess == true);
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
        else if (_dragFrom is { } grabbed)
        {
            var at = e.GetPosition(Root);
            SetZoom(_zoom, _pan + (at - grabbed));
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
