using Avalonia.Controls;
using Avalonia.Interactivity;
using MomentFinder.App.ViewModels;

namespace MomentFinder.App.Views;

/// <summary>What goes where in the cloud (an upload, a copy from another storage, a move), before it starts.</summary>
public partial class CloudPutPanel : UserControl, IOverlayPanel
{
    public CloudPutPanel() => InitializeComponent();

    public event Action? Closed;
    private bool _closed;

    public CloudPutPanel(CloudPutViewModel vm) : this()
    {
        // The form first: set after the panel's own, it would for a moment inherit this view model.
        NewStorage.DataContext = vm.Origin.Form;
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

    /// <summary>A click on a folder of the other storage goes into it.</summary>
    private async void OnFolderChosen(object? sender, SelectionChangedEventArgs e)
    {
        if (Vm?.Origin is not { NotBusy: true } origin || FolderList.SelectedItem is not MomentFinder.Cloud.StorageFolder folder) return;
        FolderList.SelectedItem = null;
        await origin.OpenAsync(folder.Name);
    }
}
