#!/usr/bin/env bash
# macOS packages of MediaLens - for each architecture (arm64 = Apple Silicon, x64 = Intel):
# publishes the application (self-contained), adds ffmpeg, ffprobe and rclone (Contents/MacOS/tools), builds MediaLens.app,
# signs it with the Developer ID certificate, packs it into a .dmg, has Apple notarize the .dmg and staples the
# notarization ticket to it.
#   build/out/MediaLens-<version>-osx-arm64.dmg
#   build/out/MediaLens-<version>-osx-x64.dmg
#
#   bash build/macos/build.sh            # version from Directory.Build.props
#   bash build/macos/build.sh 0.2.0      # or given
#
# Must run on macOS (Microsoft-hosted macos-latest agent). Environment variables:
#   MAC_SIGN_IDENTITY   "Developer ID Application: Your Name (TEAMID)" - the certificate must be in the keychain
#                       (import the .p12 into a keychain first)
#   NOTARY_KEY_PATH     path to the App Store Connect API key (.p8)
#   NOTARY_KEY_ID       its Key ID
#   NOTARY_ISSUER_ID    its Issuer ID
# Without MAC_SIGN_IDENTITY the .dmg files are built unsigned (only for testing - macOS will block them).
set -euo pipefail

# ---------- bundled tools (pinned) ----------
# FFmpeg, static GPL builds:
#   Apple Silicon: https://www.osxexperts.net (the page shows the SHA-256 of the binary inside each zip)
#   Intel:         https://evermeet.cx/ffmpeg/ (each zip has a GPG signature, .sig)
# The values below are the SHA-256 of the zip files; for a new version: shasum -a 256 <zip>.
FFMPEG_VERSION="9.0.2"
FFMPEG_ARM64_URL="https://www.osxexperts.net/ffmpeg902arm.zip"
FFMPEG_ARM64_SHA256="4597f0f3a397cc0027bfd03dc4dd9c3066be15943cc788370846b4305d178bcb"
FFPROBE_ARM64_URL="https://www.osxexperts.net/ffprobe902arm.zip"
FFPROBE_ARM64_SHA256="c765d316eb210f3c98de6f9688df784552f3f8f586090e2a8cb871be9003164e"
FFMPEG_X64_URL="https://evermeet.cx/ffmpeg/ffmpeg-9.0.2.zip"
FFMPEG_X64_SHA256="4acc0be580f9b2788029eb7bd4d645ff87968911b0a62aeeb3940d42d54558d5"
FFPROBE_X64_URL="https://evermeet.cx/ffmpeg/ffprobe-9.0.2.zip"
FFPROBE_X64_SHA256="24a9c968cd4da72d99c7245e914b921815835eb6dff01d99868031aebaf1d439"
# rclone: https://downloads.rclone.org/v<version>/SHA256SUMS
RCLONE_VERSION="1.76.0"
RCLONE_ARM64_SHA256="e72dc90b70f4947a7b72b61bde4a4fbba5777b2874fb448951ffdde67ebd0ab7"
RCLONE_X64_SHA256="4f5835733d52b818340f97b1fc0348a70461534d123363a183ca2b660219595a"

ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
VERSION="${1:-$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' "$ROOT/Directory.Build.props" | head -n 1)}"
[ -n "$VERSION" ] || { echo "No version given and none found in Directory.Build.props" >&2; exit 1; }
OUT="$ROOT/build/out"
HERE="$ROOT/build/macos"
DOWNLOADS="$OUT/downloads"
# Spaces around the identity (easily copied into the variable group with it) would make codesign not find the certificate.
MAC_SIGN_IDENTITY="$(printf '%s' "${MAC_SIGN_IDENTITY:-}" | sed -e 's/^[[:space:]]*//' -e 's/[[:space:]]*$//')"

# fetch <file name in the cache> <url> <sha256>: downloads once into build/out/downloads, checks the hash every time.
fetch() {
    local file="$DOWNLOADS/$1" url="$2" sha="$3"
    mkdir -p "$DOWNLOADS"
    if [ ! -f "$file" ] || ! echo "$sha  $file" | shasum -a 256 -c -s; then
        echo "   downloading $url"
        curl -fsSL --retry 3 -o "$file.part" "$url"
        mv "$file.part" "$file"
        if ! echo "$sha  $file" | shasum -a 256 -c -s; then
            echo "SHA-256 of $url does not match ($sha expected, $(shasum -a 256 "$file" | cut -d' ' -f1) downloaded)" >&2
            rm -f "$file"
            exit 1
        fi
    fi
}

# ---------- icon (MediaLens.icns from the 1024 px logo) ----------
ICONSET="$OUT/macos/MediaLens.iconset"
rm -rf "$ICONSET"; mkdir -p "$ICONSET"
for SIZE in 16 32 128 256 512; do
    sips -z $SIZE $SIZE "$ROOT/build/assets/icon-1024.png" --out "$ICONSET/icon_${SIZE}x${SIZE}.png" >/dev/null
    DOUBLE=$((SIZE * 2))
    sips -z $DOUBLE $DOUBLE "$ROOT/build/assets/icon-1024.png" --out "$ICONSET/icon_${SIZE}x${SIZE}@2x.png" >/dev/null
done
iconutil -c icns "$ICONSET" -o "$OUT/macos/MediaLens.icns"

# Every signature asks Apple's timestamp service, hundreds of times per build - it sometimes answers "The timestamp
# service is not available" or "A timestamp was expected but was not found", or does not answer for minutes.
# Up to 6 attempts, 15, 30, 60, 120 and 240 s apart (almost 8 minutes); each failure also prints whether the service
# answers at all.
retry() { # retry <command>...
    local attempt wait=15
    for attempt in 1 2 3 4 5; do
        "$@" && return 0
        echo "   failed (attempt $attempt) - again in $wait s; timestamp.apple.com: $(curl -sS -m 20 -o /dev/null \
            -w 'HTTP %{http_code} in %{time_total} s' http://timestamp.apple.com/ts01 2>&1)"
        sleep $wait
        wait=$((wait * 2))
    done
    "$@"
}

sign() { # sign <path> [extra codesign options]
    [ -n "${MAC_SIGN_IDENTITY:-}" ] || return 0
    local target="$1"; shift
    retry codesign --force --timestamp --options runtime --entitlements "$HERE/entitlements.plist" \
        --sign "$MAC_SIGN_IDENTITY" "$@" "$target"
}

for ARCH in arm64 x64; do
    RID="osx-$ARCH"
    PUBLISH="$OUT/publish/$RID"
    APP="$OUT/macos/$RID/MediaLens.app"
    DMG="$OUT/MediaLens-$VERSION-$RID.dmg"

    echo "== Publishing MediaLens $VERSION for $RID"
    rm -rf "$PUBLISH"
    dotnet publish "$ROOT/src/MediaLens.App/MediaLens.App.csproj" -c Release -r "$RID" --self-contained true \
        -p:Version="$VERSION" -p:DebugType=None -o "$PUBLISH"

    echo "== Adding ffmpeg $FFMPEG_VERSION, ffprobe and rclone $RCLONE_VERSION ($ARCH)"
    if [ "$ARCH" = arm64 ]; then
        fetch "ffmpeg-$FFMPEG_VERSION-osx-arm64.zip" "$FFMPEG_ARM64_URL" "$FFMPEG_ARM64_SHA256"
        fetch "ffprobe-$FFMPEG_VERSION-osx-arm64.zip" "$FFPROBE_ARM64_URL" "$FFPROBE_ARM64_SHA256"
        RCLONE_ARCH=arm64; RCLONE_SHA256="$RCLONE_ARM64_SHA256"; FFMPEG_FROM="https://www.osxexperts.net/"
    else
        fetch "ffmpeg-$FFMPEG_VERSION-osx-x64.zip" "$FFMPEG_X64_URL" "$FFMPEG_X64_SHA256"
        fetch "ffprobe-$FFMPEG_VERSION-osx-x64.zip" "$FFPROBE_X64_URL" "$FFPROBE_X64_SHA256"
        RCLONE_ARCH=amd64; RCLONE_SHA256="$RCLONE_X64_SHA256"; FFMPEG_FROM="https://evermeet.cx/ffmpeg/"
    fi
    RCLONE_NAME="rclone-v$RCLONE_VERSION-osx-$RCLONE_ARCH"
    fetch "$RCLONE_NAME.zip" "https://downloads.rclone.org/v$RCLONE_VERSION/$RCLONE_NAME.zip" "$RCLONE_SHA256"
    TOOLS="$PUBLISH/tools"
    mkdir -p "$TOOLS"
    unzip -q -o -j "$DOWNLOADS/ffmpeg-$FFMPEG_VERSION-$RID.zip" ffmpeg -d "$TOOLS"
    unzip -q -o -j "$DOWNLOADS/ffprobe-$FFMPEG_VERSION-$RID.zip" ffprobe -d "$TOOLS"
    unzip -q -o -j "$DOWNLOADS/$RCLONE_NAME.zip" "$RCLONE_NAME/rclone" -d "$TOOLS"
    chmod +x "$TOOLS/ffmpeg" "$TOOLS/ffprobe" "$TOOLS/rclone"
    cat > "$TOOLS/THIRD-PARTY.txt" <<EOF
Programs in this folder are not part of MediaLens; they are distributed unchanged under their own licenses.

FFmpeg $FFMPEG_VERSION (ffmpeg, ffprobe)
  License: GNU GPL ("ffmpeg -L" prints it)
  Binaries: $FFMPEG_FROM
  Source code: https://ffmpeg.org/releases/ffmpeg-$FFMPEG_VERSION.tar.xz

rclone $RCLONE_VERSION
  License: MIT, https://github.com/rclone/rclone/blob/master/COPYING
  Binaries: https://downloads.rclone.org/v$RCLONE_VERSION/
  Source code: https://github.com/rclone/rclone/tree/v$RCLONE_VERSION
EOF

    echo "== Building MediaLens.app ($ARCH)"
    rm -rf "$APP"
    mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
    cp -a "$PUBLISH/." "$APP/Contents/MacOS/"
    chmod +x "$APP/Contents/MacOS/MediaLens"
    sed "s/__VERSION__/$VERSION/g" "$HERE/Info.plist" > "$APP/Contents/Info.plist"
    cp "$OUT/macos/MediaLens.icns" "$APP/Contents/Resources/MediaLens.icns"

    if [ -n "${MAC_SIGN_IDENTITY:-}" ]; then
        echo "== Signing ($ARCH)"
        # Every file inside first (native libraries, the bundled tools, but also the .NET DLLs and JSON files - anything in
        # Contents/MacOS counts as code for the bundle signature), then the executable, then the bundle.
        find "$APP/Contents/MacOS" -type f ! -name MediaLens -print0 | while IFS= read -r -d '' FILE; do
            sign "$FILE"
        done
        sign "$APP/Contents/MacOS/MediaLens"
        sign "$APP"
        codesign --verify --deep --strict "$APP"
    else
        # Apple Silicon runs only signed code: an ad-hoc signature lets the tools of a test build run on this Mac.
        for TOOL in ffmpeg ffprobe rclone; do
            codesign --force --sign - "$APP/Contents/MacOS/tools/$TOOL"
        done
    fi
    # On a Mac of the same architecture: a quick check that the bundled tools run.
    if [ "$(uname -m)" = "$( [ "$ARCH" = x64 ] && echo x86_64 || echo arm64 )" ]; then
        "$APP/Contents/MacOS/tools/ffmpeg" -hide_banner -version | head -n 1
        "$APP/Contents/MacOS/tools/ffprobe" -hide_banner -version | head -n 1
        "$APP/Contents/MacOS/tools/rclone" version | head -n 1
    fi

    echo "== Building the .dmg ($ARCH)"
    STAGE="$OUT/macos/$RID/dmg"
    rm -rf "$STAGE" "$DMG"; mkdir -p "$STAGE"
    cp -a "$APP" "$STAGE/"
    ln -s /Applications "$STAGE/Applications"
    hdiutil create -volname "MediaLens" -srcfolder "$STAGE" -ov -format UDZO "$DMG"
    # The stage has the link to /Applications - anything that walks build/out later would walk the whole /Applications.
    rm -rf "$STAGE"

    if [ -n "${MAC_SIGN_IDENTITY:-}" ]; then
        retry codesign --force --timestamp --sign "$MAC_SIGN_IDENTITY" "$DMG"
        echo "== Notarizing ($ARCH) - this takes a few minutes"
        xcrun notarytool submit "$DMG" --key "$NOTARY_KEY_PATH" --key-id "$NOTARY_KEY_ID" --issuer "$NOTARY_ISSUER_ID" --wait
        xcrun stapler staple "$DMG"
        xcrun stapler validate "$DMG"
    fi
done

echo "== Done:"
ls -l "$OUT"/MediaLens-"$VERSION"-osx-*.dmg
