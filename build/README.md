# Release build of MediaLens

Each platform has one script that publishes the application **self-contained** (nobody has to install .NET), adds the external tools and builds the packages. The scripts run by hand or from the GitHub workflows ([`.github/workflows`](../.github/workflows)), which only call them.

| Platform | Runs on | Script | Result in `build/out/` |
|---|---|---|---|
| Windows | Windows (`windows-latest`) | `build/windows/build.ps1` | `MediaLens-<version>-win-x64.exe` (Inno Setup, installs into `Program Files\MediaLens`, Start menu shortcut), `MediaLens-<version>-win-x64.zip` (the same files, unzip and run) |
| Linux | Linux (`ubuntu-latest`) | `build/linux/build.sh` | `MediaLens-<version>-linux-x64.AppImage`, `MediaLens-<version>-linux-x64.deb` |
| macOS | macOS (`macos-latest`) | `build/macos/build.sh` | `MediaLens-<version>-osx-arm64.dmg`, `MediaLens-<version>-osx-x64.dmg`, signed and notarized |

Everything goes to `build/out/` (ignored by git): the packages right in it, the publish folders, the app bundles and the download cache of the tools in subfolders.

## Commands

```
pwsh build/windows/build.ps1                  # on Windows; version from Directory.Build.props
pwsh build/windows/build.ps1 -Version 0.2.0   # or given
bash build/linux/build.sh                     # on Linux
bash build/linux/build.sh 0.2.0
ORT_CUDA=true bash build/linux/build.sh       # ONNX Runtime with CUDA (NVIDIA), files named ...-linux-x64-cuda.*
bash build/macos/build.sh                     # on macOS; without MAC_SIGN_IDENTITY the .dmg files are unsigned
bash build/macos/build.sh 0.2.0
```

The version is `<Version>` of `Directory.Build.props` unless given; it is passed to `dotnet publish -p:Version=...` and becomes part of the file names. Use `major.minor.patch`.

## Prerequisites

| Platform | Needs |
|---|---|
| all | .NET 9 SDK, internet access (NuGet, the tool downloads) |
| Windows | PowerShell 7 (`pwsh`), Inno Setup 6 (installed with Chocolatey when missing); for signing the .NET 8 runtime |
| Linux | `dpkg-deb`, `curl`, `unzip`, `tar`, `xz`, `sha256sum` (all on Ubuntu); `appimagetool` is downloaded |
| macOS | Xcode command line tools (`codesign`, `xcrun notarytool`, `stapler`, `sips`, `iconutil`, `hdiutil`); for signing the Developer ID certificate in the keychain |

## What goes into the packages

- **The application**: `src/MediaLens.App`, published with `-c Release -r <rid> --self-contained true`. The runtime identifier selects ONNX Runtime (Windows: DirectML; macOS and Linux: CPU, on macOS with CoreML; Linux with `ORT_CUDA=true`: CUDA) and libVLC (Windows and macOS: from NuGet, inside the package).
- **Linux libVLC**: from the distribution. The `.deb` depends on `libvlc5` and `vlc-plugin-base`; the AppImage needs `vlc` (or `libvlc5` and `vlc-plugin-base`) installed.
- **Tools** in `tools/` next to the executable (the application looks there first): `ffmpeg`, `ffprobe` and `rclone`, plus `THIRD-PARTY.txt` with their licenses and sources. The versions, URLs and SHA-256 values are pinned at the top of each script; a download whose hash differs stops the build.

| Tool | Windows | Linux | macOS |
|---|---|---|---|
| FFmpeg 9.0.2 (GPL v3) | [gyan.dev](https://www.gyan.dev/ffmpeg/builds/) essentials, from its [GitHub mirror](https://github.com/GyanD/codexffmpeg/releases) | [BtbN FFmpeg-Builds](https://github.com/BtbN/FFmpeg-Builds/releases), release branch, month-end autobuild | arm64: [osxexperts.net](https://www.osxexperts.net), x64: [evermeet.cx](https://evermeet.cx/ffmpeg/) |
| rclone 1.76.0 (MIT) | [downloads.rclone.org](https://downloads.rclone.org/) | same | same |

To update a tool, change the version, file name and SHA-256 at the top of the script (the providers publish the hashes: gyan.dev `<file>.sha256`, BtbN `checksums.sha256`, rclone `SHA256SUMS`; for the macOS FFmpeg zips run `shasum -a 256 <zip>`). Only take FFmpeg builds without `--enable-nonfree` (`ffmpeg -L` says "not legally redistributable" for those). For Linux take a month-end BtbN autobuild (`autobuild-YYYY-MM-31-...`): those stay for about two years, the daily ones only for two weeks.

- **The AI models are not bundled**: the application downloads them on first use.
- **Icon**: `build/assets/` has a placeholder icon (`icon-1024.png` for macOS, `icon-256.png` for Linux, `app.ico` for the Windows installer and shortcuts). Replace the files to change it; the scripts pick them up.

## Signing

### Windows: Azure Artifact Signing

Unsigned, the installer works, but SmartScreen shows "Windows protected your PC" and Smart App Control may block the application. Signed, it shows **Letsnet s.r.o.** as the publisher. `build/windows/build.ps1` downloads SignTool and the Artifact Signing plugin from NuGet and signs `MediaLens.exe`, every DLL without a signature of its own (Avalonia, libVLC plugins, ...) and the bundled `ffmpeg.exe`, `ffprobe.exe` and `rclone.exe`; the installer, its uninstaller and the setup engine are signed by Inno Setup itself (`SignTool` in `MediaLens.iss`).

Signing starts when `ARTIFACT_SIGNING_ACCOUNT` is set; then all of these are needed:

| Environment variable (in GitHub: environment `release`) | Value | Secret |
|---|---|---|
| `ARTIFACT_SIGNING_ENDPOINT` | endpoint of the region of the account, West Europe = `https://weu.codesigning.azure.net` | no (variable) |
| `ARTIFACT_SIGNING_ACCOUNT` | the Artifact Signing account | no (variable) |
| `ARTIFACT_SIGNING_PROFILE` | its certificate profile (Public Trust) | no (variable) |
| `AZURE_TENANT_ID` | Directory (tenant) ID of the app registration | no (variable) |
| `AZURE_CLIENT_ID` | Application (client) ID | no (variable) |
| `AZURE_CLIENT_SECRET` | its client secret | yes (secret) |

The app registration needs the role **Artifact Signing Certificate Profile Signer** on the certificate profile. An endpoint of another region than the account fails with 403.

### macOS: Developer ID and notarization

macOS opens an application downloaded from the internet only when it is signed with a **Developer ID Application** certificate and notarized by Apple. `build/macos/build.sh` signs every file in the bundle (the .NET libraries, libVLC and its plugins, ONNX Runtime and the tools in `Contents/MacOS/tools`) with the hardened runtime and `entitlements.plist`, then the bundle, then the `.dmg`, submits the `.dmg` for notarization and staples the ticket.

| Environment variable | Value |
|---|---|
| `MAC_SIGN_IDENTITY` | `Developer ID Application: Name (TEAMID)`, the certificate must be in the keychain |
| `NOTARY_KEY_PATH` | path to the App Store Connect API key (`.p8`) |
| `NOTARY_KEY_ID` | its Key ID |
| `NOTARY_ISSUER_ID` | its Issuer ID |

The Developer ID certificate and the API key belong to the developer account, not to an application.

Bundle identifier: `cz.letsnet.MediaLens` (`macos/Info.plist`). `Info.plist` also holds the texts of the permission prompts (Photos library, Desktop, Documents, Downloads, removable and network volumes).

Without `MAC_SIGN_IDENTITY` the script builds unsigned `.dmg` files (the tools get an ad-hoc signature so that they run on Apple Silicon): only for testing, macOS blocks them on other computers.

## GitHub workflows

- **Build** (`.github/workflows/build.yml`), on every push to `main` and every pull request: the tests on Linux and an unsigned Windows installer, kept as the artifact `MediaLens-dev-win-x64` of the run.
- **Release** (`.github/workflows/release.yml`), on a tag `v<major>.<minor>.<patch>`: the signed Windows installer and zip with their SHA-256, published as the GitHub Release of the tag. Raise `<Version>` in `Directory.Build.props` with it.

```
git tag v0.2.0
git push origin v0.2.0
```

**Set up once** (Settings -> Environments -> New environment `release`): deployment tags `v*` only, the five variables and the secret of the signing table above. Without them the release is built unsigned.

Linux and macOS packages are built by hand with their scripts for now.
