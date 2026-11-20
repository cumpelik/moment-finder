# MediaLens

Photo and video browser with local AI. Everything runs on your computer: the models are downloaded once
and then work offline. Windows, Linux and macOS.

## What it does

- **Library**: folders (and cloud folders) as sources, incremental scan, EXIF and video metadata, thumbnails.
- **Search by description** in Czech or English ("lyžař", "beach at sunset 2023", "video narozeniny"),
  with filters for year (`2023`, `2019-2021`), kind (`foto`, `video`) and people by name (`Eva lyže 2024`).
  Videos are searched frame by frame; a found video shows and starts at the matching moment.
- **People across years and places**: faces are grouped; name a group once ("Who is this?") and the person
  is followed through the years (each confident match extends the person, so a child is recognized as it
  grows). Within one event (a ski day) people keep their clothes, so a skier in a helmet and goggles is
  recognized by the jacket, also through video tracks. What the analysis is unsure of goes to
  **Review suggestions** (Y / N / S / O). In the viewer, `I` shows the boxes: click one to say who it is.
- **Viewer and slideshow**: Play all, Shuffle, photos for N seconds, videos whole or only the matching part.
- **Buckets** (a photo book): `B` adds the selection, `Ctrl+Left/Right` changes the order, Copy to folder
  exports the originals numbered `001_`, `002_`...
- **Cloud and backup** (via rclone): OneDrive, Google Drive, S3 (Amazon, Backblaze, Wasabi, MinIO...) or a
  NAS folder. Mirrors upload new and changed files of a source every few hours and never delete in the cloud.
  With a password everything is encrypted before upload (rclone crypt: contents and names), and readable
  only with the password, also with plain rclone. A cloud folder can be browsed as a source (files are
  fetched on demand into a size-limited cache) or restored. The index (names, people, buckets) is backed up
  with each mirror run; Import names brings it into another library, matched by file content.
- **Apple Photos** (macOS): with "Download Originals to this Mac" the originals of the Photos library are
  regular files; Sources + offers the library (needs the Photos or Full Disk Access permission).

## Keys

| Grid | |
|---|---|
| arrows, Home, End | move (Shift extends the selection) |
| Enter, double click | open |
| P / S | play all / shuffle (the selection if more than one item is selected) |
| B | add to the active bucket |
| Delete | remove from the bucket (in a bucket); not this person (in a person's faces) |
| Ctrl+A, Ctrl+F | select all, search |
| Y / N / S / O | review: yes / no / skip / someone else |

| Viewer | |
|---|---|
| Left / Right, wheel | previous / next |
| Space | pause, play |
| Shift+Left / Right | seek 10 s |
| Up / Down | volume |
| I | people boxes (click one to name it) |
| B | add to the bucket |
| F, double click | full screen |
| Esc | close |

## Build and run

.NET 9 SDK. Video needs ffmpeg (thumbnails, frames) and libVLC (playback), the cloud needs rclone:
the installers bundle ffmpeg and rclone (and libVLC on Windows and macOS); for a development build put
them on the PATH, on Linux install `vlc` (or `libvlc5 vlc-plugin-base`).

```
dotnet run --project src/MediaLens.App
dotnet test
```

Installers: see [build/README.md](build/README.md).

AI acceleration: DirectML on Windows (any GPU), CoreML on macOS, CPU elsewhere;
on Linux with NVIDIA build with `-p:OrtCuda=true`. `MEDIALENS_DEVICE=cpu` forces the CPU.

## Command line

```
medialens add ~/Pictures
medialens models download
medialens index
medialens search lyžař 2023
medialens clusters / name <group> <name> / persons / match
medialens bucket add "Fotokniha" Eva lyže 2023
medialens bucket export "Fotokniha" ~/Desktop/fotokniha
medialens cloud add-onedrive Backup --password ...
medialens mirror add <source> <cloud> / mirror run
medialens cloud restore <cloud> <folder> <local folder> / cloud import-index <cloud>
```

(`dotnet run --project src/MediaLens.Cli -- <command>` in a development build.)
The library lives in the user's local application data (`MediaLens/`); `MEDIALENS_DATA` points it elsewhere.

## Layout

| Project | |
|---|---|
| MediaLens.Core | SQLite index, scanner, thumbnails, vector index, search, buckets, people matching, cloud |
| MediaLens.Analysis | ONNX Runtime models (SigLIP, faces, people), model download, `LibraryHost` |
| MediaLens.App | Avalonia UI, libVLC player |
| MediaLens.Cli | the same library without UI |

Scene embeddings are float16 rows in `vectors/scene.vec`, scanned exactly with SIMD; SQLite maps rows to
media and evaluates the filters first. Face and body embeddings are stored with their detections.

## Models

| Model | Use | License |
|---|---|---|
| SigLIP 2 base patch16 224 (ONNX by onnx-community) | search by description | Apache 2.0 |
| Helsinki-NLP OPUS-MT cs-en (ONNX by Xenova) | Czech queries translated to English first, which the search model understands much better | Apache 2.0 |
| InsightFace buffalo_l: SCRFD 10G + ArcFace R50 | faces | weights for non-commercial use only |
| YOLOX-s | people detection | Apache 2.0 |
| OSNet-AIN x1.0 MSMT17 | recognizing people by clothes | MIT |
| GeoNames cities1000 (data, not a model) | place names for GPS positions, offline | CC BY 4.0 (credit GeoNames) |

Before any commercial release the face models must be replaced (for example YuNet for detection and a
face recognition model with a commercial license).
