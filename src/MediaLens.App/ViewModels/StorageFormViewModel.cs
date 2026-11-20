using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MediaLens.Cloud;

namespace MediaLens.App.ViewModels;

/// <summary>A kind of storage, as offered where a cloud is connected.</summary>
public sealed record StorageKind(string Title, string Provider, string AddressLabel, string AddressHint)
{
    public bool IsS3 => Provider.Length > 0;
    /// <summary>IDrive e2 tells its address by the access key: nothing to fill in.</summary>
    public bool NeedsAddress => AddressLabel.Length > 0;
    public override string ToString() => Title;
}

/// <summary>
/// The fields of a storage to connect (kind, address, keys, bucket or folder), for the library's cloud in Settings and
/// for a folder's cloud. Connecting is the caller's: what happens next differs.
/// </summary>
public sealed partial class StorageFormViewModel : ObservableObject
{
    private const string Address = "Address (endpoint)";

    public IReadOnlyList<StorageKind> Kinds { get; } =
    [
        new("IDrive e2", "IDrive", "", ""),
        new("S3 compatible (other)", "Other", Address, "e.g. s3.example.com"),
        new("Amazon S3", "AWS", "Region", "e.g. eu-central-1"),
        new("Backblaze B2", "Other", Address, "e.g. s3.eu-central-003.backblazeb2.com"),
        new("Wasabi", "Wasabi", Address, "e.g. s3.eu-central-1.wasabisys.com"),
        new("MinIO", "Minio", Address, "e.g. https://minio.example.com:9000"),
        new("OneDrive", "", "", ""),
        new("Google Drive", "", "", ""),
        new("Folder (NAS, external disk)", "", "", ""),
    ];

    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsS3), nameof(IsFolder), nameof(IsSignIn), nameof(NeedsAddress), nameof(KeysHint))] private StorageKind _kind;
    [ObservableProperty] private string _endpoint = "";
    [ObservableProperty] private string _accessKey = "";
    [ObservableProperty] private string _secretKey = "";
    [ObservableProperty] private string _bucket;
    [ObservableProperty] private string _folder = "";

    public bool IsS3 => Kind.IsS3;
    public bool NeedsAddress => Kind.IsS3 && Kind.NeedsAddress;
    /// <summary>Where the keys come from, for the kind that needs nothing else.</summary>
    public string KeysHint => Kind.Provider == "IDrive" ? "The two keys are in IDrive e2 under Access Keys. The address is found by them." : "";
    public bool IsFolder => Kind.Title.StartsWith("Folder", StringComparison.Ordinal);
    public bool IsSignIn => !IsS3 && !IsFolder;
    public bool RcloneMissing => !Rclone.IsAvailable;

    public StorageFormViewModel(string bucket)
    {
        _kind = Kinds[0];
        _bucket = bucket;
    }

    /// <summary>What is missing in the form, in words; null when it can be connected.</summary>
    public string? Missing()
    {
        if (IsS3 && (NeedsAddress && string.IsNullOrWhiteSpace(Endpoint) || string.IsNullOrWhiteSpace(AccessKey) ||
                     string.IsNullOrWhiteSpace(SecretKey) || string.IsNullOrWhiteSpace(Bucket)))
            return NeedsAddress ? $"Fill in the {Kind.AddressLabel.ToLowerInvariant()}, both keys and the bucket." : "Fill in both keys and the bucket.";
        if (IsFolder && string.IsNullOrWhiteSpace(Folder)) return "Choose the folder.";
        return null;
    }

    public string ConnectingText => IsSignIn ? "Sign in in the browser window that opened..." : "Connecting...";

    /// <summary>
    /// Connects the storage. It stores files as they are: what has to be unreadable there is encrypted on its own way
    /// in (the access keys of the clouds always, a folder when the user chose so).
    /// </summary>
    public async Task<CloudInfo> ConnectAsync(CloudService service, CancellationToken ct)
    {
        var kind = Kind;
        var aws = kind.Provider == "AWS";
        var cloud = kind.IsS3
            ? await service.AddS3Async(kind.Title, new S3Settings(kind.Provider, aws || !kind.NeedsAddress ? "" : Endpoint.Trim(), aws ? Endpoint.Trim() : "",
                AccessKey.Trim(), SecretKey.Trim(), Bucket.Trim()), null, ct, encrypt: false)
            : kind.Title == "OneDrive" ? await service.AddOneDriveAsync(kind.Title, null, ct, encrypt: false)
            : kind.Title == "Google Drive" ? await service.AddGoogleDriveAsync(kind.Title, null, ct, encrypt: false)
            : await service.AddFolderAsync("Folder", Folder.Trim(), null, ct, encrypt: false);
        SecretKey = "";
        return cloud;
    }
}

/// <summary>Cloud work started from a window: one thing at a time, its state in words, and a way to stop it.</summary>
public abstract partial class CloudWorkViewModel : ObservableObject
{
    private CancellationTokenSource? _cts;

    [ObservableProperty] private string _status = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(NotBusy))] private bool _busy;
    public bool NotBusy => !Busy;

    protected abstract void Reload();

    protected async Task RunAsync(string what, Func<CancellationToken, Task> action)
    {
        _cts = new CancellationTokenSource();
        Busy = true;
        Status = what;
        try
        {
            await action(_cts.Token);
        }
        catch (OperationCanceledException)
        {
            Status = "Stopped.";
        }
        catch (Exception e) when (e is RcloneException or IOException or HttpRequestException or InvalidOperationException)
        {
            Status = "Failed: " + Plain(e.Message);
        }
        finally
        {
            Busy = false;
            Reload();
        }
    }

    [RelayCommand]
    private void Stop() => _cts?.Cancel();

    /// <summary>What a storage's answer means, in words; the answer itself stays at the end for whoever needs it.</summary>
    public static string Plain(string error)
    {
        // rclone's own stamp ("2026/11/20 20:25:32 NOTICE: ") says nothing here.
        error = System.Text.RegularExpressions.Regex.Replace(error.Trim(), @"^(Error: )?\d{4}/\d\d/\d\d \d\d:\d\d:\d\d (NOTICE|ERROR)?\s*:?\s*", "");
        if (error.LastIndexOf("last error was:", StringComparison.Ordinal) is >= 0 and var last) error = error[(last + 15)..].Trim();
        bool Has(string part) => error.Contains(part, StringComparison.OrdinalIgnoreCase);
        var meaning =
            Has("SignatureDoesNotMatch") ? "The secret key does not belong to this access key."
            : Has("InvalidAccessKeyId") ? "The storage does not know this access key."
            : Has("BucketAlreadyExists") ? "A bucket of this name belongs to somebody else: choose another name."
            : Has("NoSuchBucket") ? "There is no such bucket, and it could not be made."
            : Has("AccessDenied") || Has("Forbidden") ? "The key is not allowed to do this. It has to read and write the bucket (and to make it, if it is not there)."
            : Has("send request failed") || Has("no such host") || Has("dial tcp") || Has("connection refused") ? "The storage does not answer. Check the address and the internet connection."
            : null;
        return meaning == null ? error : $"{meaning} ({error.Trim()})";
    }
}
