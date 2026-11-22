using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using MomentFinder.App.ViewModels;

namespace MomentFinder.App.Views;

/// <summary>
/// The browser of this computer's (or another storage's) folders and files: what is selected in it is dragged onto
/// a folder of the library in the cloud.
/// </summary>
public partial class FileBrowserPanel : UserControl
{
    private PointerPressedEventArgs? _press;
    private Point _pressedAt;
    private BrowseEntry? _pressed;
    /// <summary>Pressed on one of several selected entries: they stay selected for the drag; a plain click selects it alone.</summary>
    private bool _keptSelection;

    /// <summary>The button to connect another storage: the main window shows the form over itself.</summary>
    public event Action? ConnectRequested;

    public FileBrowserPanel()
    {
        InitializeComponent();
        List.AddHandler(PointerPressedEvent, OnPressed, RoutingStrategies.Tunnel);
        List.AddHandler(PointerMovedEvent, OnMoved, RoutingStrategies.Tunnel);
        List.AddHandler(PointerReleasedEvent, OnReleased, RoutingStrategies.Tunnel);
    }

    private FileBrowserViewModel? Vm => DataContext as FileBrowserViewModel;

    private void OnConnect(object? sender, RoutedEventArgs e) => ConnectRequested?.Invoke();

    private static BrowseEntry? EntryOf(RoutedEventArgs e) =>
        (e.Source as Visual)?.GetSelfAndVisualAncestors().OfType<ListBoxItem>().FirstOrDefault()?.DataContext as BrowseEntry;

    private async void OnDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (Vm is { } vm && EntryOf(e) is { IsFolder: true } entry) await vm.OpenAsync(entry);
    }

    private void OnPressed(object? sender, PointerPressedEventArgs e)
    {
        _press = null;
        _keptSelection = false;
        if (!e.GetCurrentPoint(List).Properties.IsLeftButtonPressed || EntryOf(e) is not { } entry) return;
        _press = e;
        _pressed = entry;
        _pressedAt = e.GetPosition(List);
        // The list would select the pressed entry alone at once: several selected ones could never be dragged together.
        if (e.KeyModifiers == KeyModifiers.None && List.SelectedItems is { Count: > 1 } selected && selected.Contains(entry))
        {
            _keptSelection = true;
            e.Handled = true;
        }
    }

    private async void OnMoved(object? sender, PointerEventArgs e)
    {
        if (_press is not { } press || _pressed is not { } entry || Vm is not { } vm) return;
        var moved = e.GetPosition(List) - _pressedAt;
        if (Math.Abs(moved.X) < 6 && Math.Abs(moved.Y) < 6) return;
        _press = null;
        _keptSelection = false;
        var selected = List.SelectedItems?.OfType<BrowseEntry>().ToList() ?? [];
        // In the order the list shows them.
        var entries = selected.Contains(entry) ? vm.Entries.Where(selected.Contains).ToList() : [entry];
        var data = new DataTransfer();
        data.Add(DataTransferItem.Create(BrowseDrag.Format, vm.Drag(entries)));
        await DragDrop.DoDragDropAsync(press, data, DragDropEffects.Copy);
    }

    private void OnReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_keptSelection && _pressed is { } entry) List.SelectedItem = entry;
        _press = null;
        _keptSelection = false;
    }
}
