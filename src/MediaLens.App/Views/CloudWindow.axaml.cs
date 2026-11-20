using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using MediaLens.App.ViewModels;

namespace MediaLens.App.Views;

public partial class CloudWindow : Window
{
    public CloudWindow()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (Vm != null) Vm.RestartRequested += Restart;
        };
    }

    /// <summary>The restored library takes its place at the start: this one ends and a new one opens.</summary>
    private static void Restart()
    {
        if (Environment.ProcessPath is { } exe) System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe) { UseShellExecute = false });
        (Avalonia.Application.Current?.ApplicationLifetime as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)?.Shutdown();
    }

    private async void OnCopyRecovery(object? sender, RoutedEventArgs e)
    {
        if (Vm is not { RecoveryText.Length: > 0 } vm || Clipboard == null) return;
        await Clipboard.SetTextAsync(vm.RecoveryText);
        vm.Status = "The recovery string is in the clipboard. Paste it where you keep passwords.";
    }

    private async void OnSaveRecovery(object? sender, RoutedEventArgs e)
    {
        if (Vm is not { RecoveryText.Length: > 0 } vm) return;
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save the recovery string", SuggestedFileName = "MediaLens recovery string.txt", DefaultExtension = "txt",
        });
        if (file?.TryGetLocalPath() is not { } path) return;
        await File.WriteAllTextAsync(path, vm.RecoveryText);
        vm.Status = $"Saved to {path}. Keep a copy away from this computer too.";
    }

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
