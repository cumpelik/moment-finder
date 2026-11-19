#!/usr/bin/env bash
# Linux packages of MediaLens: publishes the application (self-contained, linux-x64), adds ffmpeg, ffprobe and rclone
# (static builds, tools/ next to the executable) and builds
#   build/out/MediaLens-<version>-linux-x64.AppImage   (runs on most distributions, no installation)
#   build/out/MediaLens-<version>-linux-x64.deb        (Debian, Ubuntu and derivatives)
#
#   bash build/linux/build.sh            # version from Directory.Build.props
#   bash build/linux/build.sh 0.2.0      # or given
#   ORT_CUDA=true bash build/linux/build.sh 0.2.0   # ONNX Runtime with CUDA (NVIDIA), files named ...-linux-x64-cuda.*
#
# Needs: dotnet SDK 9, dpkg-deb, curl, unzip, sha256sum (all on the Microsoft-hosted ubuntu-latest agent).
# Video playback uses the libVLC of the distribution: the .deb depends on it, the AppImage needs vlc installed.
set -euo pipefail

# ---------- bundled tools (pinned; the SHA-256 values are the ones the providers publish) ----------
# FFmpeg: static GPL build of the release branch from https://github.com/BtbN/FFmpeg-Builds. Take a month-end
# autobuild (autobuild-YYYY-MM-31-...): those are kept for about two years, the daily ones only for two weeks.
# The SHA-256 is in checksums.sha256 of the release (and next to each file on GitHub).
FFMPEG_VERSION="9.0.2"
FFMPEG_RELEASE="autobuild-2026-10-31-12-59"
FFMPEG_NAME="ffmpeg-n9.0.2-39-g7adfbe40dc-linux64-gpl-9.0"
FFMPEG_SHA256="73e29a56974ae5a651133a04584c7483b21afe941e17dc91d79814a758b608d3"
# rclone: https://downloads.rclone.org/v<version>/SHA256SUMS
RCLONE_VERSION="1.76.0"
RCLONE_SHA256="b00f00b3266c3764413afe599d3ec316f6f43b3e0ea8a0bc1d186d2133e46df7"

ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
VERSION="${1:-$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' "$ROOT/Directory.Build.props" | head -n 1)}"
[ -n "$VERSION" ] || { echo "No version given and none found in Directory.Build.props" >&2; exit 1; }
OUT="$ROOT/build/out"
PUBLISH="$OUT/publish/linux-x64"
DOWNLOADS="$OUT/downloads"
ICON="$ROOT/build/assets/icon-256.png"
DESKTOP="$ROOT/build/linux/medialens.desktop"

SUFFIX="linux-x64"
ORT_ARGS=()
if [ "${ORT_CUDA:-}" = "true" ]; then
    SUFFIX="linux-x64-cuda"
    ORT_ARGS=(-p:OrtCuda=true)
fi

# fetch <file name in the cache> <url> <sha256>: downloads once into build/out/downloads, checks the hash every time.
fetch() {
    local file="$DOWNLOADS/$1" url="$2" sha="$3"
    mkdir -p "$DOWNLOADS"
    if [ ! -f "$file" ] || ! echo "$sha  $file" | sha256sum -c --status; then
        echo "   downloading $url"
        curl -fsSL --retry 3 -o "$file.part" "$url"
        mv "$file.part" "$file"
        if ! echo "$sha  $file" | sha256sum -c --status; then
            echo "SHA-256 of $url does not match ($sha expected, $(sha256sum "$file" | cut -d' ' -f1) downloaded)" >&2
            rm -f "$file"
            exit 1
        fi
    fi
}

echo "== Publishing MediaLens $VERSION for linux-x64${ORT_ARGS:+ (ONNX Runtime with CUDA)}"
rm -rf "$PUBLISH"
dotnet publish "$ROOT/src/MediaLens.App/MediaLens.App.csproj" -c Release -r linux-x64 --self-contained true \
    -p:Version="$VERSION" -p:DebugType=None "${ORT_ARGS[@]}" -o "$PUBLISH"
chmod +x "$PUBLISH/MediaLens"

echo "== Adding ffmpeg $FFMPEG_VERSION, ffprobe and rclone $RCLONE_VERSION (tools/)"
fetch "$FFMPEG_NAME.tar.xz" \
    "https://github.com/BtbN/FFmpeg-Builds/releases/download/$FFMPEG_RELEASE/$FFMPEG_NAME.tar.xz" "$FFMPEG_SHA256"
fetch "rclone-v$RCLONE_VERSION-linux-amd64.zip" \
    "https://downloads.rclone.org/v$RCLONE_VERSION/rclone-v$RCLONE_VERSION-linux-amd64.zip" "$RCLONE_SHA256"
TOOLS="$PUBLISH/tools"
mkdir -p "$TOOLS"
tar -xJf "$DOWNLOADS/$FFMPEG_NAME.tar.xz" -C "$TOOLS" --strip-components=2 "$FFMPEG_NAME/bin/ffmpeg" "$FFMPEG_NAME/bin/ffprobe"
unzip -q -o -j "$DOWNLOADS/rclone-v$RCLONE_VERSION-linux-amd64.zip" "rclone-v$RCLONE_VERSION-linux-amd64/rclone" -d "$TOOLS"
chmod +x "$TOOLS/ffmpeg" "$TOOLS/ffprobe" "$TOOLS/rclone"
cat > "$TOOLS/THIRD-PARTY.txt" <<EOF
Programs in this folder are not part of MediaLens; they are distributed unchanged under their own licenses.

FFmpeg $FFMPEG_VERSION (ffmpeg, ffprobe)
  License: GNU GPL version 3 ("ffmpeg -L" prints it)
  Binaries: https://github.com/BtbN/FFmpeg-Builds/releases/tag/$FFMPEG_RELEASE ($FFMPEG_NAME)
  Source code: https://ffmpeg.org/releases/ffmpeg-$FFMPEG_VERSION.tar.xz

rclone $RCLONE_VERSION
  License: MIT, https://github.com/rclone/rclone/blob/master/COPYING
  Binaries: https://downloads.rclone.org/v$RCLONE_VERSION/
  Source code: https://github.com/rclone/rclone/tree/v$RCLONE_VERSION
EOF
# A quick check that the binaries run here (a wrong architecture or a dynamic build would fail).
"$TOOLS/ffmpeg" -hide_banner -version | head -n 1
"$TOOLS/ffprobe" -hide_banner -version | head -n 1
"$TOOLS/rclone" version | head -n 1

# ---------- AppImage ----------
echo "== Building the AppImage"
APPDIR="$OUT/linux/MediaLens.AppDir"
rm -rf "$APPDIR"
mkdir -p "$APPDIR/usr/lib/medialens"
cp -a "$PUBLISH/." "$APPDIR/usr/lib/medialens/"
cp "$DESKTOP" "$APPDIR/medialens.desktop"
cp "$ICON" "$APPDIR/medialens.png"
cat > "$APPDIR/AppRun" <<'EOF'
#!/bin/sh
HERE="$(dirname "$(readlink -f "$0")")"
exec "$HERE/usr/lib/medialens/MediaLens" "$@"
EOF
chmod +x "$APPDIR/AppRun"

TOOL="$OUT/linux/appimagetool"
if [ ! -x "$TOOL" ]; then
    curl -fsSL -o "$TOOL" https://github.com/AppImage/appimagetool/releases/download/continuous/appimagetool-x86_64.AppImage
    chmod +x "$TOOL"
fi
# --appimage-extract-and-run: works on build agents without FUSE
ARCH=x86_64 "$TOOL" --appimage-extract-and-run "$APPDIR" "$OUT/MediaLens-$VERSION-$SUFFIX.AppImage"

# ---------- .deb ----------
echo "== Building the .deb"
DEB="$OUT/linux/deb"
rm -rf "$DEB"
mkdir -p "$DEB/DEBIAN" "$DEB/opt/medialens" "$DEB/usr/bin" "$DEB/usr/share/applications" "$DEB/usr/share/icons/hicolor/256x256/apps"
cp -a "$PUBLISH/." "$DEB/opt/medialens/"
# A small launcher instead of a symbolic link: the build also works in a folder that cannot hold links (a network share).
cat > "$DEB/usr/bin/medialens" <<'EOF'
#!/bin/sh
exec /opt/medialens/MediaLens "$@"
EOF
chmod 755 "$DEB/usr/bin/medialens"
cp "$DESKTOP" "$DEB/usr/share/applications/medialens.desktop"
cp "$ICON" "$DEB/usr/share/icons/hicolor/256x256/apps/medialens.png"
SIZE_KB="$(du -s --apparent-size --block-size=1K "$DEB/opt" | cut -f1)"
cat > "$DEB/DEBIAN/control" <<EOF
Package: medialens
Version: $VERSION
Section: graphics
Priority: optional
Architecture: amd64
Maintainer: Letsnet s.r.o. <info@letsnet.cz>
Installed-Size: $SIZE_KB
Depends: libc6, libstdc++6, libfontconfig1, libx11-6, libice6, libsm6, libvlc5, vlc-plugin-base
Recommends: libicu-dev | libicu76 | libicu74 | libicu72 | libicu70
Description: Photo and video browser with local AI search
 MediaLens indexes your photo and video folders and finds pictures and moments
 in videos by a description, entirely on your computer. Includes a viewer,
 slideshows and buckets for exporting a selection.
EOF
dpkg-deb --root-owner-group --build "$DEB" "$OUT/MediaLens-$VERSION-$SUFFIX.deb"

echo "== Done:"
ls -l "$OUT"/MediaLens-"$VERSION"-"$SUFFIX".*
