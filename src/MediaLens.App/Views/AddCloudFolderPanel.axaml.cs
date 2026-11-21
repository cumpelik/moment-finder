using Avalonia.Controls;
using Avalonia.Interactivity;
using MediaLens.App.ViewModels;

namespace MediaLens.App.Views;

/// <summary>A folder in a storage added to the library as a source.</summary>
public partial class AddCloudFolderPanel : UserControl, IOverlayPanel
{
    public AddCloudFolderPanel() => InitializeComponent();

    public event Action? Closed;
    private bool _closed;

    public AddCloudFolderPanel(AddCloudFolderViewModel vm) : this()
    {
        // The form first: set after the panel's own, it would for a moment inherit this view model.
        NewStorage.DataContext = vm.Form;
        DataContext = vm;
        vm.Added += _ => Close();
    }

    private AddCloudFolderViewModel? Vm => DataContext as AddCloudFolderViewModel;

    public void Close()
    {
        if (_closed) return;
        _closed = true;
        Closed?.Invoke();
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();

    /// <summary>A click on a folder goes into it.</summary>
    private async void OnFolderChosen(object? sender, SelectionChangedEventArgs e)
    {
        if (Vm is not { NotBusy: true } vm || FolderList.SelectedItem is not MediaLens.Cloud.StorageFolder folder) return;
        FolderList.SelectedItem = null;
        await vm.OpenAsync(folder.Name);
    }
}
