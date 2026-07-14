# Changelog

All notable changes to BitBroom Rescue are documented here.
Format follows [Keep a Changelog](https://keepachangelog.com/); versioning follows [SemVer](https://semver.org/).

## [1.0.0] — 2026-07-14

First release. Safety-first, open-source data recovery for Windows 10/11.

### Engine

- **Read-only by construction**: all device access flows through `ISectorSource`, which has no write path. Raw volumes/disks are opened `GENERIC_READ` with full sharing.
- **NTFS**: full MFT walk — boot-sector parsing, fixup application, resident and non-resident attributes, runlist decoding (including fragmented MFT), deleted-record detection, parent-chain path reconstruction, per-file confidence scoring.
- **FAT12/16/32**: deleted directory entries (0xE5), long names stitched from surviving LFN entries, cluster-chain walk for live dirs, contiguous fallback for deleted content, structural FAT32 identification (BPB-first, then cluster-count thresholds).
- **exFAT**: directory entry sets with the in-use bit cleared, full long names, contiguous and chained reads.
- **Recycle Bin**: `$I`/`$R` parsing (original path, deletion time, size) across per-user bin folders.
- **Volume Shadow Copies**: snapshot enumeration and previous-version lookup via GLOBALROOT paths.
- **Carving**: signature library with per-format validators (JPEG, PNG, GIF, BMP, WebP, HEIC, PDF, ZIP/DOCX/XLSX, MP3, WAV, AVI, MKV, 7z, RAR, SQLite, …) and ISO-BMFF box-aware sizing for MP4/MOV/HEIC so multi-gigabyte videos carve to their exact length.
- **Imaging**: ddrescue-style two-pass clone (fast pass + fine retries), zero-filled bad regions, ddrescue-style `.map` file; validated on a real failing USB stick that died mid-clone (partial image remained fully scannable).
- **Health**: SSD/HDD detection (seek-penalty IOCTL), SMART failure prediction, TRIM status, and honest advisories (TRIMmed SSD ⇒ deleted content likely unrecoverable; failing SMART ⇒ clone first).
- **Recovery orchestration**: session with automatic file-system detection, destination guard (refuses same-volume and non-qualified destinations), collision-safe writer with timestamp restoration and a plain-text audit log.

### Apps

- **GUI** (`BitBroomRescue.exe`): WPF Fluent (Windows 11) dark UI — drive picker with health advisories, read-only scan with live progress, filterable results grid with color-coded confidence, select-and-recover with enforced different-drive destination, clone-to-image and open-image workflow for failing drives.
- **CLI** (`bitbroom-rescue.exe`): `list`, `scan`, `carve`, `recover`, `image`, `health`, `bin`, `previous` — the same engine, scriptable, with explicit exit codes.

### Verified

- 35 unit tests against in-memory synthetic NTFS/FAT32/exFAT images (real boot sectors, fixup, runlists, deleted entries) with byte-exact round-trips.
- Real-hardware E2E: NTFS system drive (955k+ deleted files enumerated read-only), FAT32 USB stick (deleted files found and recovered, canary file byte-exact via clone image), failing-flash clone with bad-sector map.
