using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using MomentFinder.App.ViewModels;

namespace MomentFinder.App.Views;

/// <summary>The fields of a storage to connect (see StorageFormViewModel).</summary>
public partial class StorageForm : UserControl
{
    public StorageForm() => InitializeComponent();

    private async void OnPickFolder(object? sender, RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage) return;
        var folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "The folder to keep things in" });
        if (DataContext is StorageFormViewModel vm && folders.Count > 0 && folders[0].TryGetLocalPath() is { } path) vm.Folder = path;
    }
}
