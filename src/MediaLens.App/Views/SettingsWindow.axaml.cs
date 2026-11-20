using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using MediaLens.App.ViewModels;

namespace MediaLens.App.Views;

/// <summary>Settings: how things play, and the library in a cloud with its recovery string.</summary>
public partial class SettingsWindow : Window
{
    public SettingsWindow() => InitializeComponent();

    public SettingsWindow(MainViewModel main) : this()
    {
        // The cloud part first: set after the window's own, it would for a moment inherit the main view model,
        // and its bindings (compiled for the cloud one) would fail on it.
        var cloud = main.CreateLibraryCloudViewModel();
        cloud.RestartRequested += Restart;
        CloudPanel.DataContext = cloud;
        DataContext = main;
    }

    private LibraryCloudViewModel? Cloud => CloudPanel.DataContext as LibraryCloudViewModel;

    private void OnClose(object? sender, RoutedEventArgs e) => Close();

    /// <summary>The restored library takes its place at the start: this one ends and a new one opens.</summary>
    private static void Restart()
    {
        if (Environment.ProcessPath is { } exe) System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe) { UseShellExecute = false });
        (Avalonia.Application.Current?.ApplicationLifetime as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)?.Shutdown();
    }

    private async void OnPickFolder(object? sender, RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Folder to keep the library in" });
        if (Cloud is { } vm && folders.Count > 0 && folders[0].TryGetLocalPath() is { } path) vm.Folder = path;
    }

    private async void OnCopyRecovery(object? sender, RoutedEventArgs e)
    {
        if (Cloud is not { RecoveryText.Length: > 0 } vm || Clipboard == null) return;
        await Clipboard.SetTextAsync(vm.RecoveryText);
        vm.Status = "The recovery string is in the clipboard. Paste it where you keep passwords.";
    }

    private async void OnSaveRecovery(object? sender, RoutedEventArgs e)
    {
        if (Cloud is not { RecoveryText.Length: > 0 } vm) return;
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save the recovery string", SuggestedFileName = "MediaLens recovery string.txt", DefaultExtension = "txt",
        });
        if (file?.TryGetLocalPath() is not { } path) return;
        await File.WriteAllTextAsync(path, vm.RecoveryText);
        vm.Status = $"Saved to {path}. Keep a copy away from this computer too.";
    }
}
