using Avalonia.Controls;
using Avalonia.Interactivity;
using MomentFinder.App.ViewModels;

namespace MomentFinder.App.Views;

/// <summary>The uploads and changes in the cloud the user asked for: how far each is, what did not go and why.</summary>
public partial class CloudJobsPanel : UserControl, IOverlayPanel
{
    public CloudJobsPanel() => InitializeComponent();

    public event Action? Closed;
    private bool _closed;

    public CloudJobsPanel(CloudJobsViewModel vm) : this()
    {
        DataContext = vm;
        _ = vm.ReloadAsync();
    }

    public void Close()
    {
        if (_closed) return;
        _closed = true;
        Closed?.Invoke();
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
