using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using MediaLens.App.ViewModels;

namespace MediaLens.App.Views;

public partial class CloudWindow : Window
{
    public CloudWindow() => InitializeComponent();

    private CloudViewModel? Vm => DataContext as CloudViewModel;

    private async Task<string?> PickFolderAsync(string title)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = title });
        return folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
    }

    private async void OnPickFolder(object? sender, RoutedEventArgs e)
    {
        if (Vm != null && await PickFolderAsync("Folder for the backup") is { } path) Vm.Folder = path;
    }

    private async void OnRestore(object? sender, RoutedEventArgs e)
    {
        if (Vm != null && await PickFolderAsync("Restore into") is { } path) await Vm.RestoreAsync(path);
    }
}
