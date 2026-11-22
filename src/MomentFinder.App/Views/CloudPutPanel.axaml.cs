using Avalonia.Controls;
using Avalonia.Interactivity;
using MomentFinder.App.ViewModels;

namespace MomentFinder.App.Views;

/// <summary>What goes where in the cloud (files dropped in, a move), before it starts.</summary>
public partial class CloudPutPanel : UserControl, IOverlayPanel
{
    public CloudPutPanel() => InitializeComponent();

    public event Action? Closed;
    private bool _closed;

    public CloudPutPanel(CloudPutViewModel vm) : this()
    {
        DataContext = vm;
        vm.Started += Close;
    }

    private CloudPutViewModel? Vm => DataContext as CloudPutViewModel;

    public void Close()
    {
        if (_closed) return;
        _closed = true;
        if (Vm is { } vm) vm.Started -= Close;
        Closed?.Invoke();
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
