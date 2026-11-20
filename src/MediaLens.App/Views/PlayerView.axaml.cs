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
            if (e.Delta.Y > 0) _vm?.Previous();
            else if (e.Delta.Y < 0) _vm?.Next();
        };
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

    private static readonly Avalonia.Media.Color KnownColor = Avalonia.Media.Color.FromRgb(0x34, 0xD3, 0x99);
    private static readonly Avalonia.Media.Color UnknownColor = Avalonia.Media.Color.FromRgb(0xF5, 0x9E, 0x0B);

    /// <summary>
    /// A box around each person with the name above it (inside only at the top edge of the picture), so a long
    /// name never gets cut by a narrow box. A click on the box or the name says who it is.
    /// </summary>
    private void DrawPeople()
    {
        PeopleLayer.Children.Clear();
        if (_vm == null) return;
        // Boxes are drawn in the picture's pixels and scaled with it: one screen pixel is this many of them,
        // so lines and names keep the same size on screen for any video and window.
        var shown = Math.Min(Root.Bounds.Width / _vm.ContentWidth, Root.Bounds.Height / _vm.ContentHeight);
        var scale = shown > 0 ? 1 / shown : 1;
        foreach (var box in _vm.PeopleBoxes())
        {
            var color = box.Known ? KnownColor : UnknownColor;
            var frame = new Border
            {
                Width = box.W,
                Height = box.H,
                CornerRadius = new CornerRadius(6 * scale),
                BorderBrush = new Avalonia.Media.SolidColorBrush(color),
                BorderThickness = new Thickness(2 * scale),
                Background = Avalonia.Media.Brushes.Transparent,
                BoxShadow = Avalonia.Media.BoxShadows.Parse($"0 0 {4 * scale:0} 0 #99000000"),
                Cursor = new Cursor(StandardCursorType.Hand),
            };
            var label = new Border
            {
                Background = new Avalonia.Media.SolidColorBrush(color),
                CornerRadius = new CornerRadius(10 * scale),
                Padding = new Thickness(8 * scale, 2 * scale),
                BoxShadow = Avalonia.Media.BoxShadows.Parse($"0 {1 * scale:0} {4 * scale:0} 0 #80000000"),
                Cursor = new Cursor(StandardCursorType.Hand),
                Child = new TextBlock
                {
                    Text = box.Label,
                    FontSize = 13 * scale,
                    FontWeight = Avalonia.Media.FontWeight.SemiBold,
                    Foreground = Avalonia.Media.Brushes.Black,
                },
            };
            ToolTip.SetTip(frame, "Click: who is it?");
            ToolTip.SetTip(label, "Click: who is it?");
            frame.PointerPressed += (_, e) => AskWho(e, box.DetectionId);
            label.PointerPressed += (_, e) => AskWho(e, box.DetectionId);
            Canvas.SetLeft(frame, box.X);
            Canvas.SetTop(frame, box.Y);
            Canvas.SetLeft(label, box.X);
            // Above the box; at the top edge of the picture inside it.
            if (box.Y > 28 * scale) Canvas.SetBottom(label, _vm.ContentHeight - box.Y + 4 * scale);
            else Canvas.SetTop(label, box.Y + 4 * scale);
            PeopleLayer.Children.Add(frame);
            PeopleLayer.Children.Add(label);
        }
    }

    private async void AskWho(PointerPressedEventArgs e, long detectionId)
    {
        e.Handled = true;
        if (_vm is not { } vm || TopLevel.GetTopLevel(this) is not Window owner) return;
        var wasRunning = vm.HoldForDialog();
        var name = await Dialogs.PromptNameAsync(owner, "Who is it?", vm.Names());
        if (name != null) vm.NameDetection(detectionId, name);
        vm.ResumeAfterDialog(wasRunning);
        KeyboardWanted?.Invoke();
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
