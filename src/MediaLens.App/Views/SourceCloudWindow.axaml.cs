using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using MediaLens.App.ViewModels;

namespace MediaLens.App.Views;

/// <summary>A source and the cloud: connect it, its backup, clone it to the cloud, or give a folder in the cloud a home here.</summary>
public partial class SourceCloudWindow : Window
{
    public SourceCloudWindow() => InitializeComponent();

    public SourceCloudWindow(SourceCloudViewModel vm) : this()
    {
        // The form first: set after the window's own, it would for a moment inherit this view model.
        NewStorage.DataContext = vm.Form;
        DataContext = vm;
        vm.CloneStarted += Close;
        Closed += (_, _) =>
        {
            vm.CloneStarted -= Close;
            vm.Detach();
        };
    }

    private SourceCloudViewModel? Vm => DataContext as SourceCloudViewModel;

    private void OnClose(object? sender, RoutedEventArgs e) => Close();

    private async void OnDeleteGone(object? sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;
        if (await Dialogs.ConfirmAsync(this, "Delete in the cloud",
                $"Delete {vm.GoneSummary} in the cloud? They are not in your folder any more, so after this they are gone for good."))
            await vm.DeleteGoneAsync();
    }

    private async void OnMoveToCloud(object? sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;
        if (await Dialogs.ConfirmAsync(this, "Clone to the cloud",
                $"Clone \"{vm.SourceName}\" to the cloud? Nothing on this computer is deleted. Its copy there becomes a source of its own that every computer of this library may change. " +
                "This folder stays in the library as a separate source and is not backed up any more."))
            vm.MoveToCloud();
    }

    private async void OnDisconnect(object? sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;
        var ok = vm.MoveWaiting
            ? await Dialogs.ConfirmAsync(this, "Stop the clone",
                $"Stop cloning \"{vm.SourceName}\"? What is copied so far stays in the cloud as it is; the folder here is not touched.")
            : await Dialogs.ConfirmAsync(this, "Stop the backup",
                $"Stop backing up \"{vm.SourceName}\"? The copy in the cloud stays as it is; new changes of the folder do not reach it.");
        if (ok)
            vm.Disconnect();
    }

    private async void OnMakeHome(object? sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = $"Where should \"{vm.SourceName}\" be on this computer?" });
        if (folders.Count > 0 && folders[0].TryGetLocalPath() is { } path) await vm.MakeHomeAsync(path);
    }
}
