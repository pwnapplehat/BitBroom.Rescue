# BitBroom Rescue — Safety Model

Data recovery has a unique failure mode: **the tool itself can destroy the data it is
trying to save.** Every design decision below exists to make that structurally impossible,
not merely discouraged.

## 1. The source is read-only at the type level

All device access goes through one interface:

```csharp
public interface ISectorSource : IDisposable
{
    long Length { get; }
    int SectorSize { get; }
    string Description { get; }
    int Read(long offset, byte[] buffer, int bufferOffset, int count);
    byte[] ReadBestEffort(long offset, int count);
}
```

There is no `Write`. There is no `Flush`. A code path that wanted to write to the
source drive would not compile. Raw devices are opened with `GENERIC_READ` only and
full share flags, so we never take a lock that could disturb the volume either.

This is not a UI checkbox — it is the only I/O primitive the whole engine has.

## 2. The destination guard

`RecoveryDestinationGuard.Validate(sourceRoot, destination)` runs before any recovery
write and **refuses**:

- a destination on the **same volume as the source** (recovering onto the drive you're
  recovering from overwrites the very free space that still holds your other deleted
  files);
- relative or non-fully-qualified destination paths;
- destinations that resolve inside the source root.

The GUI and CLI both call the same guard. There is no override flag.

## 3. Clone-first for failing hardware

A drive that is mechanically failing degrades with every read. The imager
(`DiskImager`) implements the ddrescue strategy:

- **Pass 1** reads fast and large, skips unreadable regions immediately (no retry
  storms on a dying head), records them in a map file;
- **Retry passes** revisit only the bad regions with sector-sized reads;
- unreadable bytes are zero-filled in the image, so the image has the source's exact
  geometry and every scan/carve works against it;
- the `.map` file records good/bad extents in a ddrescue-style format.

Result: the failing drive is stressed **once**. Every subsequent scan, carve and
recovery runs against the image file. This exact flow was validated on a real failing
USB stick that died 8 GB into its clone — the partial image still yielded every
deleted file, including a canary file planted before the failure.

## 4. Honest confidence, honest health

Every recoverable item carries a `RecoveryConfidence` (High / Good / Fair / Poor) and a
human-readable reason:

- NTFS: resident data (lives in the MFT record itself) is High; non-resident data with
  an intact runlist is Good; FAT-recovered content read contiguously after the chain
  was cleared is Fair; a lost start cluster is Poor.
- Carved files are validated per-format (JPEG entropy/structure, PNG chunk CRCs, ZIP
  central directory, MP4 box chain…) before being shown at all.

`HealthAdvisor` tells the truth about the platform:

- **SSD + TRIM enabled + deleted-file scenario** → we say plainly that content is
  likely unrecoverable and point at the Recycle Bin / Shadow Copies instead.
- **SMART predicts failure** → we tell you to image first and stop scanning the drive.
- We never show a "boost recovery chance" upsell. There is nothing to sell.

## 5. Auditable writes

`RecoveryWriter` writes only into the user-chosen destination folder:

- name collisions get ` (2)`, ` (3)` … suffixes — nothing is overwritten;
- original timestamps are restored where known;
- every file written (and every failure) is recorded in a plain-text log in the
  destination folder, so you can verify exactly what was done afterwards.

## 6. What we refuse to do, even if asked

- Write anything to the source drive (no "quick unformat", no in-place undelete —
  those patterns corrupt file systems when they go wrong).
- Recover to the source drive.
- Present unvalidated carve results as recoverable files.
- Claim overwritten or TRIMmed data can be brought back.

## Reporting a safety issue

If you find any code path that can write to a source device, treat it as a critical
bug: open an issue immediately or email the address in SECURITY.md.
