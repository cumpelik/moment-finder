using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using MediaLens.App.ViewModels;

namespace MediaLens.App.Views;

/// <summary>
/// Full-screen viewer. Keys: Left/Right previous/next, Space pause/play, Shift+Left/Right seek 10 s,
/// Up/Down volume, B bucket, I people, F full screen, Esc close. The controls show on mouse move.
/// </summary>
public partial class PlayerWindow : Window
{
    private PlayerViewModel? _vm;

    public PlayerWindow()
    {
        InitializeComponent();
        AddHandler(KeyDownEvent, OnKey, RoutingStrategies.Tunnel);
        View.FullScreenRequested += ToggleFullScreen;
        View.KeyboardWanted += TakeKeyboard;
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        _vm = DataContext as PlayerViewModel;
        if (_vm == null) return;
        if (Screens.ScreenFromWindow(this) is { } screen)
            _vm.ScreenSize = screen.Bounds.Size;
        _vm.CloseRequested += Close;
        _vm.Start();
        View.ShowOverlay();
        // The keys (Esc, arrows, space) belong to the viewer, not to the window it was opened from. Opened by
        // a double click, the release of the second click activates the main window again: once more later.
        TakeKeyboard();
        DispatcherTimer.RunOnce(TakeKeyboard, TimeSpan.FromMilliseconds(350));
    }

    protected override void OnClosed(EventArgs e)
    {
        if (_vm != null) _vm.CloseRequested -= Close;
        base.OnClosed(e);
    }

    private void TakeKeyboard()
    {
        if (!IsVisible) return;
        Activate();
        View.FocusViewer();
    }

    private void ToggleFullScreen() =>
        WindowState = WindowState == WindowState.FullScreen ? WindowState.Normal : WindowState.FullScreen;

    private void OnKey(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape: Close(); break;
            case Key.F or Key.F11: ToggleFullScreen(); break;
            default:
                if (!View.HandleKey(e)) return;
                break;
        }
        e.Handled = true;
    }
}
