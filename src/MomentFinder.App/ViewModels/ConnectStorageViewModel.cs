using CommunityToolkit.Mvvm.Input;
using MomentFinder.Cloud;

namespace MomentFinder.App.ViewModels;

/// <summary>Another storage connected only to copy from it (photos that are in another bucket, a OneDrive, a Google Drive).</summary>
public sealed partial class ConnectStorageViewModel : CloudWorkViewModel
{
    private readonly LibraryHost _host;

    public StorageFormViewModel Form { get; } = new("");

    /// <summary>Connected: the window closes, the browser shows the storage.</summary>
    public event Action<CloudInfo>? Connected;

    public ConnectStorageViewModel(LibraryHost host) => _host = host;

    protected override void Reload() { }

    [RelayCommand]
    private Task Connect()
    {
        if (Form.Missing() is { } missing)
        {
            Status = missing;
            return Task.CompletedTask;
        }
        return RunAsync(Form.ConnectingText, async ct =>
        {
            var cloud = await Form.ConnectAsync(_host.Cloud, ct);
            Status = "";
            Connected?.Invoke(cloud);
        });
    }
}
