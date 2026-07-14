# BitBroom Rescue CLI

`bitbroom-rescue.exe` — the full recovery engine, scriptable. Volume and physical-disk
targets require an elevated (Administrator) terminal; image files do not.

Every command that reads a drive is **read-only**. The only commands that write
anything are `recover` (into `--out`, guarded) and `image` (into `--out`, guarded).

## Commands

### `list`

Enumerates mounted volumes (letter, size, file system, label) and physical disks.

```
bitbroom-rescue list
```

### `scan <target> [--min-size N] [--top N]`

Metadata scan for deleted files. `<target>` is a drive letter (`E`), or a path to an
image file (`D:\card.img`). Detects NTFS / FAT12/16/32 / exFAT automatically and walks
deleted entries with path reconstruction and per-file confidence.

```
bitbroom-rescue scan E --top 50
bitbroom-rescue scan D:\sdcard.img
```

### `carve <target> [--top N]`

Signature-based carving for when metadata is gone (formatted card, corrupted FS).
Recognizes JPEG, PNG, GIF, BMP, WebP, HEIC, PDF, ZIP/DOCX/XLSX, MP4/MOV (ISO-BMFF
box-aware sizing), MP3, WAV, AVI, MKV, 7z, RAR, SQLite and more — each result must
pass a format validator before it is reported.

```
bitbroom-rescue carve E
```

### `recover <target> --out <dir> [--deleted] [--carve] [--min-size N]`

Scans and writes recoverable files to `--out`. The destination must be a **different
drive** than the source — enforced, not warned. Collisions are suffixed, timestamps
restored, and an audit log is written next to the recovered files.

```
bitbroom-rescue recover E --out D:\rescued --deleted
bitbroom-rescue recover D:\card.img --out D:\rescued --carve
```

### `image <target> --out <image.img> [--retries N]`

ddrescue-style clone-first imaging for failing drives: fast pass + fine-grained retry
passes, zero-filled bad regions, and a `.map` file of good/bad extents. Then run
`scan`/`carve`/`recover` against the image instead of the drive.

```
bitbroom-rescue image E --out D:\usb.img
bitbroom-rescue scan D:\usb.img
```

### `health <diskNumber>`

Media type (SSD/HDD), SMART failure prediction, and TRIM status, with plain-language
advisories for the recovery scenario (e.g. TRIMmed SSD ⇒ deleted content is likely
gone; failing SMART ⇒ image first).

```
bitbroom-rescue health 1
```

### `bin <driveLetter>`

Parses the Recycle Bin (`$I`/`$R` pairs) on that volume: original path, deletion time,
size. These recover byte-perfect since the content file is intact.

```
bitbroom-rescue bin C
```

### `previous <driveLetter> <relative\path>`

Lists Volume Shadow Copy (Previous Versions) snapshots containing that file. Needs
admin; VSS must have been on (it usually is for C:).

```
bitbroom-rescue previous C Users\me\Documents\report.docx
```

### `version`

Prints the CLI version.

## Exit codes

| Code | Meaning |
|---|---|
| 0 | success |
| 1 | unexpected error |
| 2 | completed with failures (some files failed to recover) |
| 3 | usage error |
| 5 | refused by a safety guard (e.g. destination on source drive) |
