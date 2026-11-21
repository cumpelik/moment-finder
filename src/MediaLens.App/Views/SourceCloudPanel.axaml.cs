using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using MediaLens.App.ViewModels;

namespace MediaLens.App.Views;

/// <summary>A source and the cloud: connect it, its backup, clone it to the cloud, or give a folder in the cloud a home here.</summary>
public partial class SourceCloudPanel : UserControl, IOverlayPanel
{
    public SourceCloudPanel() => InitializeComponent();

    /// <summary>Also closed by a clone that started: the clone shows itself in the sidebar.</summary>
    public event Action? Closed;
    private bool _closed;

    public SourceCloudPanel(SourceCloudViewModel vm) : this()
    {
        // The form first: set after the panel's own, it would for a moment inherit this view model.
        NewStorage.DataContext = vm.Form;
        DataContext = vm;
        vm.CloneStarted += Close;
    }

    private SourceCloudViewModel? Vm => DataContext as SourceCloudViewModel;

    /// <summary>The main window: the owner of the questions and the folder picker.</summary>
    private Window Owner => (Window)TopLevel.GetTopLevel(this)!;

    public void Close()
    {
        if (_closed) return;
        _closed = true;
        if (Vm is { } vm)
        {
            vm.CloneStarted -= Close;
            vm.Detach();
        }
        Closed?.Invoke();
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();

    private async void OnDeleteGone(object? sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;
        if (await Dialogs.ConfirmAsync(Owner, "Delete in the cloud",
                $"Delete {vm.GoneSummary} in the cloud? They are not in your folder any more, so after this they are gone for good."))
            await vm.DeleteGoneAsync();
    }

    private async void OnMoveToCloud(object? sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;
        if (await Dialogs.ConfirmAsync(Owner, "Clone to the cloud",
                $"Clone \"{vm.SourceName}\" to the cloud? Nothing on this computer is deleted. Its copy there becomes a source of its own that every computer of this library may change. " +
                "This folder stays in the library as a separate source and is not backed up any more."))
            vm.MoveToCloud();
    }

    private async void OnDisconnect(object? sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;
        var ok = vm.MoveWaiting
            ? await Dialogs.ConfirmAsync(Owner, "Stop the clone",
                $"Stop cloning \"{vm.SourceName}\"? What is copied so far stays in the cloud as it is; the folder here is not touched.")
            : await Dialogs.ConfirmAsync(Owner, "Stop the backup",
                $"Stop backing up \"{vm.SourceName}\"? The copy in the cloud stays as it is; new changes of the folder do not reach it.");
        if (ok)
            vm.Disconnect();
    }

    private async void OnStopMerge(object? sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;
        if (await Dialogs.ConfirmAsync(Owner, "Stop adding",
                $"Stop adding \"{vm.SourceName}\" to the cloud folder? What is copied so far stays there; the folder here is not touched."))
            vm.StopMerge(vm.SourceId);
    }

    private async void OnStopIncoming(object? sender, RoutedEventArgs e)
    {
        if (Vm is not { IncomingSourceId: { } from } vm) return;
        if (await Dialogs.ConfirmAsync(Owner, "Stop adding",
                $"Stop adding \"{vm.IncomingName}\" here? What is copied so far stays here; that folder is not touched."))
            vm.StopMerge(from);
    }

    private async void OnTurnReadOnlyOff(object? sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;
        if (await Dialogs.TurnReadOnlyOffAsync(Owner, vm.ReadOnlyLock, vm.SourceId, vm.SourceName))
            vm.ReadOnlyChanged("Read-only is off until you turn it on again or start MediaLens again.");
    }

    private async void OnMakeReadOnly(object? sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;
        if (await Dialogs.MakeReadOnlyAsync(Owner, vm.ReadOnlyLock, vm.SourceId, vm.SourceName)) vm.ReadOnlyChanged("Read-only.");
    }

    private void OnTurnReadOnlyOn(object? sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;
        vm.ReadOnlyLock.TurnOn(vm.SourceId);
        vm.ReadOnlyChanged("Read-only again.");
    }

    private async void OnUnprotect(object? sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;
        if (await Dialogs.UnprotectAsync(Owner, vm.ReadOnlyLock, vm.SourceId, vm.SourceName)) vm.ReadOnlyChanged("Not read-only any more.");
    }

    private async void OnMakeHome(object? sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;
        var folders = await Owner.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = $"Where should \"{vm.SourceName}\" be on this computer?" });
        if (folders.Count > 0 && folders[0].TryGetLocalPath() is { } path) await vm.MakeHomeAsync(path);
    }
}
