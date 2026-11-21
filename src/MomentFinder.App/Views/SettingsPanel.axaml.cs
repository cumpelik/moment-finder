using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using MomentFinder.App.ViewModels;

namespace MomentFinder.App.Views;

/// <summary>Settings: how things play, and the library in a cloud with its recovery string.</summary>
public partial class SettingsPanel : UserControl, IOverlayPanel
{
    public SettingsPanel() => InitializeComponent();

    public event Action? Closed;
    private LibraryCloudViewModel? _cloud;

    public SettingsPanel(MainViewModel main) : this()
    {
        // The cloud part first: set after the window's own, it would for a moment inherit the main view model,
        // and its bindings (compiled for the cloud one) would fail on it.
        var cloud = main.CreateLibraryCloudViewModel();
        cloud.RestartRequested += Restart;
        LibraryForm.DataContext = cloud.Form;
        CloudPanel.DataContext = cloud;
        StatusBar.DataContext = cloud;
        DataContext = main;
        _cloud = cloud;
    }

    public void Close()
    {
        if (_cloud == null) return;
        _cloud.Detach();
        _cloud = null;
        Closed?.Invoke();
    }

    /// <summary>The main window: the owner of the file picker and the clipboard.</summary>
    private TopLevel Owner => TopLevel.GetTopLevel(this)!;

    private LibraryCloudViewModel? Cloud => CloudPanel.DataContext as LibraryCloudViewModel;

    private void OnClose(object? sender, RoutedEventArgs e) => Close();

    /// <summary>The restored library takes its place at the start: this one ends and a new one opens.</summary>
    private static void Restart()
    {
        if (Environment.ProcessPath is { } exe) System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe) { UseShellExecute = false });
        (Avalonia.Application.Current?.ApplicationLifetime as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)?.Shutdown();
    }

    private async void OnCopyRecovery(object? sender, RoutedEventArgs e)
    {
        if (Cloud is not { RecoveryText.Length: > 0 } vm || Owner.Clipboard is not { } clipboard) return;
        await clipboard.SetTextAsync(vm.RecoveryText);
        vm.Status = "The recovery string is in the clipboard. Paste it where you keep passwords.";
    }

    private async void OnSaveRecovery(object? sender, RoutedEventArgs e)
    {
        if (Cloud is not { RecoveryText.Length: > 0 } vm) return;
        var file = await Owner.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save the recovery string", SuggestedFileName = "MomentFinder recovery string.txt", DefaultExtension = "txt",
        });
        if (file?.TryGetLocalPath() is not { } path) return;
        await File.WriteAllTextAsync(path, vm.RecoveryText);
        vm.Status = $"Saved to {path}. Keep a copy away from this computer too.";
    }
}
