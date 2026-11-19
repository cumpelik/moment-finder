# MediaLens

Photo and video browser with local AI search. Everything runs on your computer: the models are downloaded
once and then work offline. Windows, Linux and macOS.

## What works now

- Folders as sources, incremental scan (new, changed, missing files), EXIF / video metadata, thumbnails.
- Search by description in Czech or English ("lyžař", "beach at sunset 2023", "video narozeniny"),
  filters for year (`2023`, `2019-2021`) and kind (`foto`, `video`). Videos are searched frame by frame;
  a found video shows and starts at the matching moment.
- Viewer and slideshow: Play all, Shuffle, photos for N seconds, videos whole or only the matching part.
- Buckets (e.g. for a photo book): `B` adds the selection, `Ctrl+Left/Right` changes the order,
  Copy to folder exports the originals numbered `001_`, `002_`...

## Keys

| Grid | |
|---|---|
| arrows, Home, End | move (Shift extends the selection) |
| Enter, double click | open |
| P / S | play all / shuffle (the selection if more than one item is selected) |
| B | add to the active bucket |
| Delete | remove from the bucket (in a bucket) |
| Ctrl+A, Ctrl+F | select all, search |

| Viewer | |
|---|---|
| Left / Right, wheel | previous / next |
| Space | pause, play |
| Shift+Left / Right | seek 10 s |
| Up / Down | volume |
| B | add to the bucket |
| F, double click | full screen |
| Esc | close |

## Build and run

.NET 9 SDK. Video needs ffmpeg (thumbnails, frames) and libVLC (playback):
Windows and macOS builds bundle libVLC; on Linux install `vlc` (or `libvlc5 vlc-plugin-base`) and `ffmpeg`.

```
dotnet run --project src/MediaLens.App
dotnet test
```

AI acceleration: DirectML on Windows (any GPU), CoreML on macOS, CPU elsewhere;
on Linux with NVIDIA build with `-p:OrtCuda=true`. `MEDIALENS_DEVICE=cpu` forces the CPU.

## Command line

```
dotnet run --project src/MediaLens.Cli -- add ~/Pictures
dotnet run --project src/MediaLens.Cli -- models download
dotnet run --project src/MediaLens.Cli -- index
dotnet run --project src/MediaLens.Cli -- search lyžař 2023
dotnet run --project src/MediaLens.Cli -- bucket add "Fotokniha" lyžař 2023
dotnet run --project src/MediaLens.Cli -- bucket export "Fotokniha" ~/Desktop/fotokniha
```

The library lives in the user's local application data (`MediaLens/`); `MEDIALENS_DATA` points it elsewhere.

## Layout

| Project | |
|---|---|
| MediaLens.Core | SQLite index, scanner, thumbnails, flat vector index, search, buckets |
| MediaLens.Analysis | ONNX Runtime, SigLIP 2 model, model download, `LibraryHost` |
| MediaLens.App | Avalonia UI, libVLC player |
| MediaLens.Cli | the same library without UI |

Embeddings are not in SQLite: they are float16 rows in `vectors/scene.vec`, scanned exactly with SIMD
(tens of milliseconds for 200k rows). SQLite maps rows to media and evaluates the filters first.

## Models

| Model | Use | License |
|---|---|---|
| SigLIP 2 base patch16 224 (ONNX export by onnx-community) | search by description | Apache 2.0 |
