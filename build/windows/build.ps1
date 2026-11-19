# Windows packages of MediaLens: publishes the application (self-contained, win-x64), adds ffmpeg, ffprobe and rclone
# (tools\ next to MediaLens.exe), optionally signs it and builds
#   build\out\MediaLens-<version>-win-x64.exe   (installer, Inno Setup, into Program Files\MediaLens)
#   build\out\MediaLens-<version>-win-x64.zip   (the same files; unzip and run)
#
#   pwsh build/windows/build.ps1                  # version from Directory.Build.props
#   pwsh build/windows/build.ps1 -Version 0.2.0   # or given
#
# Signing (optional) with Azure Artifact Signing - only when ARTIFACT_SIGNING_ACCOUNT is set (build/README.md, Signing):
#   ARTIFACT_SIGNING_ENDPOINT   endpoint of the region of the account, e.g. https://weu.codesigning.azure.net
#   ARTIFACT_SIGNING_ACCOUNT    the signing account
#   ARTIFACT_SIGNING_PROFILE    the certificate profile
#   AZURE_TENANT_ID, AZURE_CLIENT_ID, AZURE_CLIENT_SECRET   the app registration with the role Artifact Signing Certificate Profile Signer
# MediaLens.exe, the bundled tools and the unsigned DLLs are signed here; the installer, its uninstaller and its setup engine
# by Inno Setup (SignTool in MediaLens.iss).
# Without the variables nothing is signed.
param(
    [string] $Version
)
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue' # the progress bar makes Invoke-WebRequest many times slower

# ---------- bundled tools (pinned; the SHA-256 values are the ones the providers publish) ----------
# FFmpeg: "essentials" release build of https://www.gyan.dev/ffmpeg/builds/ (GPL v3), from its GitHub mirror where every
# version stays; the SHA-256 is on gyan.dev (<file>.sha256) and next to the file on GitHub.
$FfmpegVersion = '9.0.2'
$FfmpegSha256 = '60f467265b1e312373dbcd92200c2618a74850f98d3d078e94296bb3fa2047ba'
# rclone: https://downloads.rclone.org/v<version>/SHA256SUMS
$RcloneVersion = '1.76.0'
$RcloneSha256 = '8b4fd2737ade4e70c057ef4a49deb7cffe94b0fb9d7587c189d149823f21a1dc'

$root = Resolve-Path "$PSScriptRoot\..\.."
if (-not $Version) {
    $Version = (Select-Xml -Path "$root\Directory.Build.props" -XPath '//Version').Node.InnerText
    if (-not $Version) { throw "No -Version given and none found in Directory.Build.props" }
}
$out = Join-Path $root 'build\out'
$publish = Join-Path $out 'publish\win-x64'
$downloads = Join-Path $out 'downloads'

Write-Host "== Publishing MediaLens $Version for win-x64"
if (Test-Path $publish) { Remove-Item -Recurse -Force $publish }
dotnet publish "$root\src\MediaLens.App\MediaLens.App.csproj" -c Release -r win-x64 --self-contained true `
    -p:Version=$Version -p:DebugType=None -o $publish
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

# Downloads once into build\out\downloads and checks the SHA-256 every time.
function Fetch([string] $name, [string] $url, [string] $sha256) {
    New-Item -ItemType Directory -Force $downloads | Out-Null
    $file = Join-Path $downloads $name
    if ((Test-Path $file) -and (Get-FileHash -Algorithm SHA256 $file).Hash -eq $sha256) { return $file }
    Write-Host "   downloading $url"
    Invoke-WebRequest -Uri $url -OutFile "$file.part"
    Move-Item -Force "$file.part" $file
    $actual = (Get-FileHash -Algorithm SHA256 $file).Hash
    if ($actual -ne $sha256) {
        Remove-Item -Force $file
        throw "SHA-256 of $url does not match ($sha256 expected, $actual downloaded)"
    }
    return $file
}

# Extracts the entries with the given file names (wherever they are in the archive) into $dest.
function ExtractFiles([string] $zipFile, [string[]] $names, [string] $dest) {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = [IO.Compression.ZipFile]::OpenRead($zipFile)
    try {
        foreach ($name in $names) {
            $entry = $zip.Entries | Where-Object { $_.Name -eq $name } | Select-Object -First 1
            if (-not $entry) { throw "$name not found in $zipFile" }
            [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, (Join-Path $dest $name), $true)
        }
    } finally {
        $zip.Dispose()
    }
}

Write-Host "== Adding ffmpeg $FfmpegVersion, ffprobe and rclone $RcloneVersion (tools\)"
$ffmpegZip = Fetch "ffmpeg-$FfmpegVersion-essentials_build.zip" `
    "https://github.com/GyanD/codexffmpeg/releases/download/$FfmpegVersion/ffmpeg-$FfmpegVersion-essentials_build.zip" $FfmpegSha256
$rcloneZip = Fetch "rclone-v$RcloneVersion-windows-amd64.zip" `
    "https://downloads.rclone.org/v$RcloneVersion/rclone-v$RcloneVersion-windows-amd64.zip" $RcloneSha256
$toolsDir = Join-Path $publish 'tools'
New-Item -ItemType Directory -Force $toolsDir | Out-Null
ExtractFiles $ffmpegZip @('ffmpeg.exe', 'ffprobe.exe') $toolsDir
ExtractFiles $rcloneZip @('rclone.exe') $toolsDir
@"
Programs in this folder are not part of MediaLens; they are distributed unchanged under their own licenses.

FFmpeg $FfmpegVersion (ffmpeg.exe, ffprobe.exe)
  License: GNU GPL version 3 ("ffmpeg -L" prints it)
  Binaries: https://www.gyan.dev/ffmpeg/builds/ (ffmpeg-$FfmpegVersion-essentials_build)
  Source code: https://ffmpeg.org/releases/ffmpeg-$FfmpegVersion.tar.xz

rclone $RcloneVersion
  License: MIT, https://github.com/rclone/rclone/blob/master/COPYING
  Binaries: https://downloads.rclone.org/v$RcloneVersion/
  Source code: https://github.com/rclone/rclone/tree/v$RcloneVersion
"@ | Set-Content -Encoding utf8 (Join-Path $toolsDir 'THIRD-PARTY.txt')
# A quick check that the binaries run here.
& "$toolsDir\ffmpeg.exe" -hide_banner -version | Select-Object -First 1
& "$toolsDir\ffprobe.exe" -hide_banner -version | Select-Object -First 1
& "$toolsDir\rclone.exe" version | Select-Object -First 1
# The icon for the shortcuts and Apps & features (MediaLens.exe has no icon of its own yet).
Copy-Item "$root\build\assets\app.ico" (Join-Path $publish 'MediaLens.ico')

# The value of an environment variable; an undefined pipeline variable arrives as the literal text "$(Name)" - that is none too.
function Setting([string] $name) {
    $value = [Environment]::GetEnvironmentVariable($name)
    if ($value -and -not $value.StartsWith('$(')) { return $value }
    return $null
}

$signing = $null
if (Setting 'ARTIFACT_SIGNING_ACCOUNT') {
    foreach ($name in 'ARTIFACT_SIGNING_ENDPOINT', 'ARTIFACT_SIGNING_PROFILE', 'AZURE_TENANT_ID', 'AZURE_CLIENT_ID', 'AZURE_CLIENT_SECRET') {
        if (-not (Setting $name)) { throw "ARTIFACT_SIGNING_ACCOUNT is set, but $name is missing (build/README.md, Signing)" }
    }
    # SignTool of the Windows SDK and the Artifact Signing plugin for it, from NuGet - nothing has to be installed on the agent.
    Write-Host "== Downloading the signing tools"
    $tools = Join-Path $out 'signing-tools'
    New-Item -ItemType Directory -Force $tools | Out-Null
    $nuget = Join-Path $tools 'nuget.exe'
    if (-not (Test-Path $nuget)) { Invoke-WebRequest -Uri 'https://dist.nuget.org/win-x86-commandline/latest/nuget.exe' -OutFile $nuget }
    # The signing plugin is pinned: 1.0.151 (2026-11-03) runs on .NET 10, the pipeline installs the .NET 8 runtime for it.
    foreach ($package in @(@('Microsoft.Windows.SDK.BuildTools', $null), @('Microsoft.ArtifactSigning.Client', '1.0.146'))) {
        $name, $version = $package
        $versionArgs = @()
        if ($version) {
            # -ExcludeVersion keeps an older download of another version, so a pinned package is downloaded again.
            Remove-Item -Recurse -Force (Join-Path $tools $name) -ErrorAction SilentlyContinue
            $versionArgs = @('-Version', $version)
        }
        & $nuget install $name @versionArgs -ExcludeVersion -NonInteractive -Verbosity quiet -OutputDirectory $tools
        if ($LASTEXITCODE -ne 0) { throw "nuget install $name failed" }
    }
    $signTool = Get-ChildItem -Recurse "$tools\Microsoft.Windows.SDK.BuildTools" -Filter signtool.exe | Where-Object { $_.Directory.Name -eq 'x64' } | Select-Object -First 1
    $dlib = Get-ChildItem -Recurse "$tools\Microsoft.ArtifactSigning.Client" -Filter Azure.CodeSigning.Dlib.dll | Where-Object { $_.Directory.Name -eq 'x64' } | Select-Object -First 1
    if (-not $signTool -or -not $dlib) { throw "signtool.exe or Azure.CodeSigning.Dlib.dll (x64) not found in the NuGet packages" }
    # Signs in as the app registration through AZURE_TENANT_ID / AZURE_CLIENT_ID / AZURE_CLIENT_SECRET only.
    $metadata = Join-Path $tools 'metadata.json'
    @{
        Endpoint = Setting 'ARTIFACT_SIGNING_ENDPOINT'
        CodeSigningAccountName = Setting 'ARTIFACT_SIGNING_ACCOUNT'
        CertificateProfileName = Setting 'ARTIFACT_SIGNING_PROFILE'
        ExcludeCredentials = @('ManagedIdentityCredential', 'WorkloadIdentityCredential', 'SharedTokenCacheCredential', 'VisualStudioCredential',
            'VisualStudioCodeCredential', 'AzureCliCredential', 'AzurePowerShellCredential', 'AzureDeveloperCliCredential', 'InteractiveBrowserCredential')
    } | ConvertTo-Json | ForEach-Object { [IO.File]::WriteAllText($metadata, $_) } # without BOM - the plugin does not read a BOM
    $signing = @{ SignTool = $signTool.FullName; Dlib = $dlib.FullName; Metadata = $metadata }
} else {
    Write-Host "== ARTIFACT_SIGNING_ACCOUNT not set - nothing is signed"
}

function Sign([string[]] $files) {
    if (-not $signing) { return }
    Write-Host "== Signing $($files.Count) file(s):"
    $files | ForEach-Object { Write-Host "   $_" }
    # In batches: libVLC brings hundreds of plugin DLLs and one command line holds at most 32767 characters.
    for ($i = 0; $i -lt $files.Count; $i += 50) {
        $batch = $files[$i..([Math]::Min($i + 49, $files.Count - 1))]
        & $signing.SignTool sign /v /fd SHA256 /tr 'http://timestamp.acs.microsoft.com' /td SHA256 /dlib $signing.Dlib /dmdf $signing.Metadata @batch
        if ($LASTEXITCODE -ne 0) { throw "signing failed" }
    }
}

# MediaLens.exe and every .exe/.dll without a signature: ours (MediaLens.dll, ...), the unsigned libraries (Avalonia,
# libVLC plugins, ...) and the bundled tools (ffmpeg.exe, ffprobe.exe, rclone.exe). Smart App Control checks each DLL the
# application loads and each program it starts, and one unsigned file blocks it ("Part of this app has been blocked").
# The files signed by Microsoft, VideoLAN, ... keep their own signature.
if ($signing) {
    Sign @(Get-ChildItem -Recurse $publish -Include *.exe, *.dll |
        Where-Object { (Get-AuthenticodeSignature $_.FullName).Status -eq 'NotSigned' } |
        ForEach-Object { $_.FullName })
}

# The same files in a ZIP (folder MediaLens inside) for those who do not want to install.
Write-Host "== Building the ZIP"
$zipStage = Join-Path $out 'zip\win-x64'
if (Test-Path $zipStage) { Remove-Item -Recurse -Force $zipStage }
New-Item -ItemType Directory -Force "$zipStage\MediaLens" | Out-Null
Copy-Item -Recurse "$publish\*" "$zipStage\MediaLens"
$zipFile = Join-Path $out "MediaLens-$Version-win-x64.zip"
if (Test-Path $zipFile) { Remove-Item -Force $zipFile }
Compress-Archive -Path "$zipStage\MediaLens" -DestinationPath $zipFile -CompressionLevel Optimal

Write-Host "== Building the installer"
$iscc = Get-Command iscc -ErrorAction SilentlyContinue
if (-not $iscc) {
    $candidates = @("${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", "$env:ProgramFiles\Inno Setup 6\ISCC.exe")
    $path = $candidates | Where-Object { Test-Path $_ } | Select-Object -First 1
    if (-not $path) {
        Write-Host "Inno Setup not found - installing it with Chocolatey"
        choco install innosetup -y --no-progress
        $path = $candidates | Where-Object { Test-Path $_ } | Select-Object -First 1
    }
    $iscc = $path
} else {
    $iscc = $iscc.Source
}
$isccArgs = @("/DAppVersion=$Version", "/DSourceDir=$publish", "/DOutputDir=$out")
if ($signing) {
    # Inno Setup signs by itself (SignTool in MediaLens.iss): the installer, the uninstaller and the setup engine extracted
    # into %TEMP% as MediaLens-<version>-win-x64.tmp. Signing only the finished installer leaves that .tmp unsigned and Smart
    # App Control blocks it (error 4551). For Inno Setup $q is a quote and $f the quoted file to sign.
    $command = '$q' + $signing.SignTool + '$q sign /v /fd SHA256 /tr http://timestamp.acs.microsoft.com /td SHA256 /dlib $q' +
        $signing.Dlib + '$q /dmdf $q' + $signing.Metadata + '$q $f'
    $isccArgs += "/Sartifact=$command", '/DSign'
}
& $iscc @isccArgs "$PSScriptRoot\MediaLens.iss"
if ($LASTEXITCODE -ne 0) { throw "Inno Setup failed" }

$installer = Join-Path $out "MediaLens-$Version-win-x64.exe"
Write-Host "== Done: $installer, $zipFile"
