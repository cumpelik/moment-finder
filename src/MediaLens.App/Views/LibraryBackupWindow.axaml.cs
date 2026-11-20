using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using MediaLens.App.ViewModels;

namespace MediaLens.App.Views;

/// <summary>Settings, Library backup: which cloud keeps the library, the recovery string, and bringing a library back.</summary>
public partial class LibraryBackupWindow : Window
{
    /// <summary>The clouds are added in the Backup window.</summary>
    public event Action? AddCloudRequested;

    public LibraryBackupWindow()
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

    private void OnAddCloud(object? sender, RoutedEventArgs e)
    {
        AddCloudRequested?.Invoke();
        Close();
    }
}
