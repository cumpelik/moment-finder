# How MomentFinder works

What each part does, in detail. The short version is in the [README](../README.md).

- Finding: [Library](#library), [Search by description](#search-by-description),
  [People](#people-across-years-and-places), [Best moments](#best-moments), [Moved files](#moved-files)
- Looking: [Viewer and slideshow](#viewer-and-slideshow), [Buckets](#buckets),
  [360 photos and videos](#360-photos-and-videos), [Apple Photos](#apple-photos)
- Cloud: [The library in a cloud](#the-library-in-a-cloud), [A folder and the cloud](#a-folder-and-the-cloud),
  [A folder in the cloud as a source](#a-folder-in-the-cloud-as-a-source),
  [Videos in the cloud](#videos-in-the-cloud), [Changing a folder that lives in the cloud](#changing-a-folder-that-lives-in-the-cloud),
  [Read-only sources](#read-only-sources)
- More computers: [Recovery string](#recovery-string), [One library for the family](#one-library-for-the-family)

## Library

Folders (and cloud folders) are the sources: incremental scan, EXIF and video metadata, thumbnails.
Folders on this computer are watched: two minutes after the last change a folder is scanned again (and its
backup follows); a disk connected later is scanned too.

## Search by description

In Czech or English ("lyžař", "beach at sunset 2023", "video narozeniny"),
with filters for year (`2023`, `2019-2021`), kind (`foto`, `video`) and people by name (`Eva lyže 2024`).
Videos are searched frame by frame; a found video shows and starts at the matching moment.

## People across years and places

Faces are grouped; name a group once ("Who is this?") and the person
is followed through the years (each confident match extends the person, so a child is recognized as it
grows). Within one event (a ski day) people keep their clothes, so a skier in a helmet and goggles is
recognized by the jacket, also through video tracks. What the analysis is unsure of goes to
**Review suggestions**: the guesses for one person are shown together ("Is this Eva?") with the whole photo of the
one you point at below them (when and where it was taken), you click the ones that are somebody else and answer for
the rest at once. Somebody not named yet who looks like a named person (a brother,
a sister) is not guessed photo by photo: the group is asked about once in "Who is this?".
In the viewer, **Who is who** shows the boxes: click one to say who it is.

A photo without a date of its own (a camera whose clock was never set, a scan) takes the date its folder or file
name says: `2007/01_07_2007_Chorvatsko`, `2008_10 Anglie`, `2005_Svatba`, `IMG-20190102-WA0001.jpg`.

## Best moments

In the sidebar: videos where somebody you named is big on the screen for a few seconds, the
best first; they play just those moments. Worked out from what the people analysis knows (who, how big, how
long), nothing is analyzed again.

## Moved files

A photo or video moved or renamed (also to another source) is recognized by its content
and keeps its thumbnail, people, buckets and analysis.

## Viewer and slideshow

Play all, Shuffle, photos for N seconds, videos whole or only the matching part.

## Buckets

A bucket is a photo book in the making: Add to bucket puts the selection in, `Ctrl+Left/Right` changes the
order, Copy to folder exports the originals numbered `001_`, `002_`...

## 360 photos and videos

Insta360 .insv / .insp originals: drag to look around, the wheel zooms; the search
and the people analysis look at six views around, and with only someone's moments the view turns to them.
The camera's gyroscope and accelerometer (in the file's trailer) level the picture: the horizon stays level and
people upright however the selfie stick is held, also for the analysis. Somebody riding away is looked at
closer (a narrow view from the lenses at full detail) and lost pieces of their way are joined, so the view can
follow one person through a whole run. Two 4K fisheye streams do not decode in
real time everywhere, so a light playing copy (one leveled 2880 x 1440 picture, encoded with the graphics card
where ffmpeg has one) is made in the background, or first when a video is opened, with its progress and a Stop.

## Apple Photos

On macOS, with "Download Originals to this Mac" the originals of the Photos library are
regular files; Sources + offers the library (needs the Photos or Full Disk Access permission).

## The library in a cloud

In Settings (via rclone). The library itself, that is index (names, people, buckets,
sources, settings), search vectors, thumbnails and the clouds' settings, is kept in a storage you give there:
IDrive e2 (two keys; its address is found by them) or another S3 compatible storage (address, two keys), with
a bucket that is made if it is not there,
Amazon S3, OneDrive, Google Drive or a folder. It is stored there as it is (it is of use to MomentFinder only);
the one thing that must not be readable, the access keys of your clouds, is encrypted on this computer before
the upload (rclone crypt) with a key the application makes itself.

## A folder and the cloud

The cloud button of a source in the sidebar connects a folder with a storage,
encrypted or not (per folder, a key made by the application and kept in the library), in one of three ways:

- *Backup*: the folder stays the main one. What is added is uploaded, what is moved or renamed is moved in the
  cloud too (same size and time at a new place: nothing is uploaded again), what is deleted waits for the
  user's word (delete in the cloud too, or bring back). Compared two minutes after a change and every hour.
  Other computers of the library see it only for looking; one of them can take it over (download, then it is
  its folder and backup).
- *Clone to the cloud* (nothing here is deleted): the clone is in the sidebar at once, with how far its copy is
  (its cloud button stops it); once all files are there it is a source of its own that every computer may
  change; the folder here stays as a separate source and its files get their twins'
  thumbnails and analysis without being read again. Keep it on this computer again: download, backed up.
- *Add it to a folder you have in the cloud* (several folders made one): the files are copied into a cloud
  folder of the library, into a folder of their own inside it or straight in. Only added: nothing there is deleted
  or written over, a file already there is skipped, another file of the same name goes beside it as `name (2).jpg`.
  The folder here stays as it is; a break (a disk away, the app closed) goes on where it stopped.

A small file in the cloud folder (`.medialens-folder.json`) says which way it is and whose it is.

## A folder in the cloud as a source

SOURCES +, Folder in the cloud: photos put into a storage by other means
(its own app, another program) are added from there, browsing its folders; files are fetched when needed.

## Videos in the cloud

A video in the cloud plays while it comes: it starts in about a second and can be moved in, the whole file
is not downloaded first (rclone serves the folder to the player on this computer only, over several connections,
decrypting on the way). A video without sound, such as a time-lapse, needs its first 15 seconds before it moves.
360 videos are still downloaded whole (leveling them needs the whole file), and so is any video the player
cannot read this way. Thumbnails of cloud videos are read from the stream too.

## Changing a folder that lives in the cloud

Upload (its button above the photos): the folders of the source
show as a tree on the left and a browser of this computer (or of another storage: another bucket, a OneDrive, a
Google Drive) on the right; files and whole folders are dragged from it onto the folder they should be in, or onto
a new one (right click in the tree). Files dragged in from the file manager go the same way. Photos are moved by
dragging them onto a folder of the tree (or Move), whole folders are moved, renamed and deleted from the tree's
menu, photos deleted with Delete.

Each is done in the background, file by file, several at once: a break goes on where it stopped, a file that
did not go is kept with its reason and tried again, and what still does not go waits in the list (status bar,
and a line above the photos). Nothing is written over: the same file is left, another one of that name goes
beside it as `name (2).jpg`. An uploaded photo is read from the file here, not downloaded again; a moved one
keeps everything known of it (people, buckets), on every computer of the library. Deleting is for good.

## Read-only sources

Right click a source, or its cloud window: nothing in it is deleted, moved or added from
MomentFinder and it is not removed from the library, until read-only is turned off with the password (one for all,
chosen the first time); it is read-only again when turned on or at the next start. A guard against mistakes in
MomentFinder, not against whoever has the storage's keys.

## Recovery string

One string (Settings, Show my recovery string) holds the way to that storage and that key.
On a second or new computer, paste it (Settings, Your library on this computer too, also offered by an empty
library): the library is copied there in the background (the main window shows how far, it goes on after a
restart) and opens at the next start, as it was. Folders that are not on that computer are browsed from the cloud,
files fetched into a size-limited cache when opened. Whoever has the string gets into the storage.

## One library for the family

Settings, Share this library: several computers use one library at the same
time. Every folder in the cloud (backed up, cloned or added from the cloud) shows on all of them; removed on one, it
is removed on all (the files stay). Names, who is (not) on a face, buckets and their order are the same everywhere.

How it is done: each computer sends its changes as files of its own into `.medialens/shared/journal/<computer>/`
and takes in the others' every minute; of two changes of one thing the later counts. Which files a folder has is
said by the computer that puts them into the cloud, so every computer shows the same photos, only those really
there, without downloading them. Thumbnails and search data are made once and stored once
(`.medialens/shared/content/`).

A computer joins with the recovery string (or "Join the shared library" when it has the cloud already): it takes
the newest summary of the library (`.medialens/shared/snapshot/`) and the changes since, and keeps its own folders
with everything found in them and what was named on it. Folders only on one computer's disk stay its own.
The backup each computer kept of its library before (`.medialens/<computer>-<id>/`) is removed by that computer
a few days after it joined, once everything it computed is in the shared library.
Each installation has its own place in a bucket (`media-lens-<id>`), so several libraries share one bucket.
