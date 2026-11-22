# MomentFinder

Photo and video browser with local AI. Everything runs on your computer: the models are downloaded once
and then work offline. Windows, Linux and macOS.

## Download

Windows: the installer is on the [Releases](../../releases/latest) page (`MomentFinder-<version>-win-x64.exe`, or the
`.zip` to unzip and run). Linux and macOS packages follow.

## What it does

- **Library**: folders (and cloud folders) as sources, incremental scan, EXIF and video metadata, thumbnails.
  Folders on this computer are watched: two minutes after the last change a folder is scanned again (and its
  backup follows); a disk connected later is scanned too.
- **Best moments** (sidebar): videos where somebody you named is big on the screen for a few seconds, the
  best first; they play just those moments. Worked out from what the people analysis knows (who, how big, how
  long), nothing is analyzed again.
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
  Amazon S3, OneDrive, Google Drive or a folder. It is stored there as it is (it is of use to MomentFinder only);
  the one thing that must not be readable, the access keys of your clouds, is encrypted on this computer before
  the upload (rclone crypt) with a key the application makes itself.
- **A folder and the cloud** (the cloud button of a source in the sidebar): a folder is connected with a storage,
  encrypted or not (per folder, a key made by the application and kept in the library), in one of two ways:
  - *Backup*: the folder stays the main one. What is added is uploaded, what is moved or renamed is moved in the
    cloud too (same size and time at a new place: nothing is uploaded again), what is deleted waits for the
    user's word (delete in the cloud too, or bring back). Compared two minutes after a change and every hour. Other computers of the library
    see it only for looking; one of them can take it over (download, then it is its folder and backup).
  - *Clone to the cloud* (nothing here is deleted): the clone is in the sidebar at once, with how far its copy is
    (its cloud button stops it); once all files are there it is a source of its own that every computer may change; the folder here stays as a separate source and its files get their twins'
    thumbnails and analysis without being read again. Keep it on this computer again: download, backed up.
  - *Add it to a folder you have in the cloud* (several folders made one): the files are copied into a cloud
    folder of the library, into a folder of their own inside it or straight in. Only added: nothing there is deleted
    or written over, a file already there is skipped, another file of the same name goes beside it as `name (2).jpg`.
    The folder here stays as it is; a break (a disk away, the app closed) goes on where it stopped.
  A small file in the cloud folder (`.medialens-folder.json`) says which way it is and whose it is.
- **A folder in the cloud as a source** (SOURCES +, Folder in the cloud): photos put into a storage by other means
  (its own app, another program) are added from there, browsing its folders; files are fetched when needed.
- **A video in the cloud plays while it comes**: it starts in about a second and can be moved in, the whole file
  is not downloaded first (rclone serves the folder to the player on this computer only, over several connections,
  decrypting on the way). A video without sound, such as a time-lapse, needs its first 15 seconds before it moves.
  360 videos are still downloaded whole (leveling them needs the whole file), and so is any video the player
  cannot read this way. Thumbnails of cloud videos are read from the stream too.
- **Changing a folder that lives in the cloud**. Upload (its button above the photos): the folders of the source
  show as a tree on the left and a browser of this computer (or of another storage: another bucket, a OneDrive, a
  Google Drive) on the right; files and whole folders are dragged from it onto the folder they should be in, or onto
  a new one (right click in the tree). Files dragged in from the file manager go the same way. Photos are moved by
  dragging them onto a folder of the tree (or Move), whole folders are moved, renamed and deleted from the tree's
  menu, photos deleted with Delete. Each is
  done in the background, file by file, several at once: a break goes on where it stopped, a file that did not go is
  kept with its reason and tried again, and what still does not go waits in the list (status bar, and a line above
  the photos). Nothing is written over: the same file is left, another one of that name goes beside it as
  `name (2).jpg`. An uploaded photo is read from the file here, not downloaded again; a moved one keeps everything
  known of it (people, buckets), on every computer of the library. Deleting is for good.
- **Read-only sources** (right click a source, or its cloud window): nothing in it is deleted, moved or added from
  MomentFinder and it is not removed from the library, until read-only is turned off with the password (one for all,
  chosen the first time); it is read-only again when turned on or at the next start. A guard against mistakes in
  MomentFinder, not against whoever has the storage's keys.
- **Recovery string**: one string (Settings, Show my recovery string) holds the way to that storage and that key.
  On a second or new computer, paste it (Settings, Your library on this computer too, also offered by an empty
  library): the library is copied there in the background (the main window shows how far, it goes on after a
  restart) and opens at the next start, as it was. Folders that are not on that computer are browsed from the cloud,
  files fetched into a size-limited cache when opened. Whoever has the string gets into the storage.
- **One library for the family** (Settings, Share this library): several computers use one library at the same
  time. Every folder in the cloud (backed up, cloned or added from the cloud) shows on all of them; removed on one, it
  is removed on all (the files stay). Names, who is (not) on a face, buckets and their order are the same everywhere:
  each computer sends its changes as files of its own into `.medialens/shared/journal/<computer>/` and takes in the
  others' every minute; of two changes of one thing the later counts. Which files a folder has is said by the
  computer that puts them into the cloud, so every computer shows the same photos, only those really there, without
  downloading them. Thumbnails and search data are made once and stored once (`.medialens/shared/content/`). A
  computer joins with the recovery string (or "Join the shared library" when it has the cloud already): it takes the
  newest summary of the library (`.medialens/shared/snapshot/`) and the changes since, and keeps its own folders
  with everything found in them and what was named on it. Folders only on one computer's disk stay its own.
  The backup each computer kept of its library before (`.medialens/<computer>-<id>/`) is removed by that computer
  a few days after it joined, once everything it computed is in the shared library.
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
| Left / Right | previous / next |
| wheel | zoom at the mouse (drag the zoomed picture to move it) |
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
dotnet run --project src/MomentFinder.App
dotnet test
```

Installers: see [build/README.md](build/README.md).

AI acceleration: DirectML on Windows (any GPU), CoreML on macOS, CPU elsewhere;
on Linux with NVIDIA build with `-p:OrtCuda=true`. `MOMENTFINDER_DEVICE=cpu` forces the CPU.

## Command line

```
momentfinder add ~/Pictures
momentfinder models download
momentfinder index
momentfinder search lyžař 2023
momentfinder clusters / name <group> <name> / persons / match
momentfinder bucket add "Fotokniha" Eva lyže 2023
momentfinder bucket export "Fotokniha" ~/Desktop/fotokniha
momentfinder cloud add-onedrive Backup --password ...
momentfinder upload <source> <file or folder>... [--into folder]
momentfinder mirror add <source> <cloud> [--encrypt] / mirror run / mirror gone|delete-gone|bring-back|to-cloud <mirror>
momentfinder source home <source> <folder>
momentfinder cloud restore <cloud> <folder> <local folder> / cloud import-index <cloud>
momentfinder backup cloud <cloud> / backup run / recovery show / recovery restore <string>
```

(`dotnet run --project src/MomentFinder.Cli -- <command>` in a development build.)
The library lives in the user's local application data (`MomentFinder/`); `MOMENTFINDER_DATA` points it elsewhere.

## Layout

| Project | |
|---|---|
| MomentFinder.Core | SQLite index, scanner, thumbnails, vector index, search, buckets, people matching, cloud |
| MomentFinder.Analysis | ONNX Runtime models (SigLIP, faces, people), model download, `LibraryHost` |
| MomentFinder.App | Avalonia UI, libVLC player |
| MomentFinder.Cli | the same library without UI |

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

## License

MIT, see [LICENSE](LICENSE). The installers bundle FFmpeg (GPL) and rclone (MIT); their licenses and sources are
listed in `tools/THIRD-PARTY.txt` next to the application. The AI models are downloaded on first use under their own
licenses.
