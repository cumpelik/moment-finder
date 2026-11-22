using Avalonia.Controls;
using Avalonia.Interactivity;
using MomentFinder.App.ViewModels;

namespace MomentFinder.App.Views;

/// <summary>Connects another storage to copy photos from.</summary>
public partial class ConnectStoragePanel : UserControl, IOverlayPanel
{
    public ConnectStoragePanel() => InitializeComponent();

    public event Action? Closed;
    private bool _closed;

    public ConnectStoragePanel(ConnectStorageViewModel vm) : this()
    {
        // The form first: set after the panel's own, it would for a moment inherit this view model.
        NewStorage.DataContext = vm.Form;
        DataContext = vm;
        vm.Connected += _ => Close();
    }

    public void Close()
    {
        if (_closed) return;
        _closed = true;
        Closed?.Invoke();
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
