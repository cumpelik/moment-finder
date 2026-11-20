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
- **The library in a cloud** (Settings; via rclone): the library itself, that is index (names, people, buckets,
  sources, settings), search vectors, thumbnails and the clouds' settings, is kept in a storage you give there:
  IDrive e2 (two keys; its address is found by them) or another S3 compatible storage (address, two keys), with
  a bucket that is made if it is not there,
  Amazon S3, OneDrive, Google Drive or a folder. It is stored there as it is (it is of use to MediaLens only);
  the one thing that must not be readable, the access keys of your clouds, is encrypted on this computer before
  the upload (rclone crypt) with a key the application makes itself.
- **A folder and the cloud** (the cloud button of a source in the sidebar): a folder is connected with a storage,
  encrypted or not (per folder, a key made by the application and kept in the library), in one of two ways:
  - *Backup*: the folder stays the main one. What is added is uploaded, what is moved or renamed is moved in the
    cloud too (same size and time at a new place: nothing is uploaded again), what is deleted waits for the
    user's word (delete in the cloud too, or bring back). Compared every hour. Other computers of the library
    see it only for looking; one of them can take it over (download, then it is its folder and backup).
  - *Move to the cloud*: after the backup is complete the copy in the cloud becomes a source of its own that
    every computer may change; the folder here stays as a separate source and its files get their twins'
    thumbnails and analysis without being read again. Keep it on this computer again: download, backed up.
  A small file in the cloud folder (`.medialens-folder.json`) says which way it is and whose it is.
- **Recovery string**: one string (Settings, Show my recovery string) holds the way to that storage and that key.
  On a new or second computer, paste it (Settings, Bring a library back, also offered by an empty library): the library comes
  back as it was, and folders that are not on that computer are browsed from the cloud, files fetched into a
  size-limited cache when opened. Whoever has the string gets into the storage.
  Each installation has its own place in a bucket (`media-lens-<id>`), so several libraries share one bucket.
- **Moved files**: a photo or video moved or renamed (also to another source) is recognized by its content
  and keeps its thumbnail, people, buckets and analysis.
- **360 photos and videos** (Insta360 .insv / .insp originals): drag to look around, the wheel zooms; the search
  and the people analysis look at six views around, and with only someone's moments the view turns to them.
  The camera's gyroscope and accelerometer (in the file's trailer) level the picture: the horizon stays level and
  people upright however the selfie stick is held, also for the analysis. Somebody riding away is looked at
  closer (a narrow view from the lenses at full detail) and lost pieces of their way are joined, so the view can
  follow one person through a whole run. Two 4K fisheye streams do not decode in
  real time everywhere, so a light playing copy (one leveled 2880 x 1440 picture, encoded with the graphics card
  where ffmpeg has one) is made in the background, or first when a video is opened, with its progress and a Stop.
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
medialens mirror add <source> <cloud> [--encrypt] / mirror run / mirror gone|delete-gone|bring-back|to-cloud <mirror>
medialens source home <source> <folder>
medialens cloud restore <cloud> <folder> <local folder> / cloud import-index <cloud>
medialens backup cloud <cloud> / backup run / recovery show / recovery restore <string>
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
