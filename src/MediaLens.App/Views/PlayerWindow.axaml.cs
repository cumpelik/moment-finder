using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using MediaLens.App.ViewModels;

namespace MediaLens.App.Views;

/// <summary>
/// Full-screen viewer. Keys: Left/Right previous/next, Space pause/play, Shift+Left/Right seek 10 s,
/// Up/Down volume, B bucket, F full screen, Esc close. The controls show on mouse move.
/// </summary>
public partial class PlayerWindow : Window
{
    private readonly DispatcherTimer _hideOverlay;
    private PlayerViewModel? _vm;
    private Point _lastPointer;

    public PlayerWindow()
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
        AddHandler(KeyDownEvent, OnKey, RoutingStrategies.Tunnel);
        Root.PointerMoved += OnPointerMoved;
        Root.PointerWheelChanged += (_, e) =>
        {
            if (e.Delta.Y > 0) _vm?.Previous();
            else if (e.Delta.Y < 0) _vm?.Next();
        };
        Root.DoubleTapped += (_, _) => ToggleFullScreen();
        SeekBar.AddHandler(PointerPressedEvent, (_, _) => _vm?.BeginSeek(), RoutingStrategies.Tunnel, handledEventsToo: true);
        SeekBar.AddHandler(PointerReleasedEvent, (_, _) => _vm?.SeekTo(SeekBar.Value), RoutingStrategies.Tunnel, handledEventsToo: true);
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        _vm = DataContext as PlayerViewModel;
        if (_vm == null) return;
        if (Screens.ScreenFromWindow(this) is { } screen)
            _vm.ScreenSize = screen.Bounds.Size;
        _vm.CloseRequested += Close;
        _vm.VideoFrameChanged += OnVideoFrame;
        _vm.PeopleChanged += DrawPeople;
        _vm.Start();
        ShowOverlay();
        // The keys (Esc, arrows, space) belong to the viewer, not to the window it was opened from. Opened by
        // a double click, the release of the second click activates the main window again: once more later.
        Root.Focusable = true;
        TakeKeyboard();
        DispatcherTimer.RunOnce(TakeKeyboard, TimeSpan.FromMilliseconds(350));
    }

    protected override void OnClosed(EventArgs e)
    {
        _hideOverlay.Stop();
        if (_vm != null)
        {
            _vm.CloseRequested -= Close;
            _vm.VideoFrameChanged -= OnVideoFrame;
            _vm.PeopleChanged -= DrawPeople;
        }
        base.OnClosed(e);
    }

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
            frame.PointerPressed += async (_, e) =>
            {
                e.Handled = true;
                var name = await Dialogs.PromptNameAsync(this, "Who is it?", _vm.Names());
                if (name != null) _vm.NameDetection(id, name);
                TakeKeyboard();
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

    private void ShowOverlay()
    {
        Overlay.Opacity = 1;
        Root.Cursor = Cursor.Default;
        _hideOverlay.Stop();
        _hideOverlay.Start();
    }

    private void TakeKeyboard()
    {
        if (!IsVisible) return;
        Activate();
        Root.Focus();
    }

    private void ToggleFullScreen() =>
        WindowState = WindowState == WindowState.FullScreen ? WindowState.Normal : WindowState.FullScreen;

    private void OnKey(object? sender, KeyEventArgs e)
    {
        if (_vm == null) return;
        var shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        switch (e.Key)
        {
            case Key.Escape: Close(); break;
            case Key.Left when shift: _vm.SeekBy(-10); break;
            case Key.Right when shift: _vm.SeekBy(10); break;
            case Key.Left or Key.PageUp: _vm.Previous(); break;
            case Key.Right or Key.PageDown: _vm.Next(); break;
            case Key.Space: _vm.TogglePlay(); ShowOverlay(); break;
            case Key.B: _vm.AddToBucket(); ShowOverlay(); break;
            case Key.I: _vm.ShowPeople = !_vm.ShowPeople; break;
            case Key.F or Key.F11: ToggleFullScreen(); break;
            case Key.Up: _vm.ChangeVolume(10); break;
            case Key.Down: _vm.ChangeVolume(-10); break;
            default: return;
        }
        e.Handled = true;
    }
}
